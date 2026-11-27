#include "mainwindow.h"
#include "runtimedata.h"
#include <QCheckBox>
#include <QComboBox>
#include <QCoreApplication>
#include <QDateTimeEdit>
#include <QDir>
#include <QDoubleSpinBox>
#include <QFile>
#include <QJsonDocument>
#include <QLabel>
#include <QMessageBox>
#include <QScopedValueRollback>
#include <QSlider>
#include <QStatusBar>
#include <QTcpSocket>

namespace {
QJsonArray vectorArray(const QVector<QDoubleSpinBox *> &spins)
{
    QJsonArray values;
    for (const auto *spin : spins)
        values.append(spin->value());
    return values;
}
}

// 先读发布时的默认 JSON，再覆盖用户保存的设置，并按 ID 合并传感器配置。
// JSON 是带字段名的数据文本格式；QJsonObject 按键存取，部分旧字段还有定向迁移逻辑。
void MainWindow::loadConfig()
{
    const QString path = QDir(QCoreApplication::applicationDirPath()).filePath("config/default_config.json");
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly)) {
        m_connectionLabel->setText("缺少 config/default_config.json");
        return;
    }
    QJsonParseError error;
    const QJsonDocument document = QJsonDocument::fromJson(file.readAll(), &error);
    if (error.error != QJsonParseError::NoError || !document.isObject()) {
        m_connectionLabel->setText("默认参数 JSON 格式错误");
        return;
    }
    const QJsonObject defaultConfig = document.object();
    m_config = m_defaultConfig = defaultConfig;
    const QJsonArray defaultSensors = defaultConfig.value("sensors").toArray();
    QFile saved(appDataPath("config/user_config.json"));
    if (saved.open(QIODevice::ReadOnly)) {
        const QJsonDocument userDocument = QJsonDocument::fromJson(saved.readAll());
        if (userDocument.isObject()) {
            const QJsonObject savedConfig = userDocument.object();
            m_config = defaultConfig;
            for (auto it = savedConfig.constBegin(); it != savedConfig.constEnd(); ++it) {
                if (it.key() == "sensors" || it.key() == "demoTrajectoryPoints" || it.key() == "demoGapM"
                    || it.key() == "demoTrajectoryEnabled" || it.key() == "solarArrayDeg"
                    || it.key() == "jointsDeg" || it.key() == "relativeDistanceM" || it.key() == "attitude"
                    || it.key() == "activePair") continue;
                if (it.key() == "orbit") {
                    QJsonObject merged = m_config.value(it.key()).toObject();
                    const QJsonObject savedObject = it.value().toObject();
                    for (auto savedField = savedObject.constBegin(); savedField != savedObject.constEnd(); ++savedField)
                        merged.insert(savedField.key(), savedField.value());
                    m_config.insert(it.key(), merged);
                } else {
                    m_config.insert(it.key(), it.value());
                }
            }
            QHash<QString, QJsonObject> savedSensors;
            const QJsonArray savedSensorArray = savedConfig.value("sensors").toArray();
            for (const auto &entry : savedSensorArray) {
                const QJsonObject sensor = entry.toObject();
                const QString id = sensor.value("id").toString();
                if (!id.isEmpty() && id != "CRDS-STARBOARD") savedSensors.insert(id, sensor);
            }
            QJsonArray mergedSensors;
            for (const auto &entry : defaultSensors) {
                QJsonObject sensor = entry.toObject();
                const QString id = sensor.value("id").toString();
                if (savedSensors.contains(id)) {
                    const QJsonObject savedSensor = savedSensors.take(id);
                    for (auto it = savedSensor.constBegin(); it != savedSensor.constEnd(); ++it)
                        sensor.insert(it.key(), it.value());
                    // 只迁移历史默认位置。已标定的旧坐标继续按原安装系解释。
                    if (!savedSensor.contains("positionReference") && savedSensor.contains("localPositionM")) {
                        const auto legacyDefault = id == "CRDS-PORT" ? QJsonArray{2.3, 0.25, 0.0}
                            : id == "TV-DOCK-WIDE" ? QJsonArray{2.3, 0.0, 0.18}
                            : id == "TV-DOCK-NARROW" ? QJsonArray{2.3, 0.0, -0.18}
                            : id == "TV-ARM-WRIST-B" ? QJsonArray{0.0, 0.12, 0.0} : QJsonArray{0.0, 0.0, 0.0};
                        if (savedSensor.value("localPositionM").toArray() == legacyDefault
                            && savedSensor.value("mount").toString(sensor.value("mount").toString())
                                == entry.toObject().value("mount").toString())
                            sensor["localPositionM"] = entry.toObject().value("localPositionM");
                        else
                            sensor["positionReference"] = "legacyMount";
                    }
                    if (id == "TV-DOCK-WIDE" && sensor.value("horizontalFovDeg").toDouble() == 74.4
                        && sensor.value("verticalFovDeg").toDouble() == 59.9) {
                        sensor["horizontalFovDeg"] = 55.0;
                        sensor["verticalFovDeg"] = 55.0;
                    } else if (id == "TV-DOCK-NARROW" && sensor.value("horizontalFovDeg").toDouble() == 10.1
                               && sensor.value("verticalFovDeg").toDouble() == 7.6) {
                        sensor["horizontalFovDeg"] = 55.0;
                        sensor["verticalFovDeg"] = 55.0;
                    } else if (id.startsWith("TV-") && sensor.value("horizontalFovDeg").toDouble() == 32.0
                               && sensor.value("verticalFovDeg").toDouble() == 24.0) {
                        sensor["horizontalFovDeg"] = 55.0;
                        sensor["verticalFovDeg"] = 55.0;
                    }
                    if (id == "TV-DOCK-WIDE" && sensor.value("name").toString() == "对接电视广角")
                        sensor["name"] = "对接电视相机 A（验证位姿）";
                    if (id == "TV-DOCK-NARROW" && sensor.value("name").toString() == "对接电视窄角")
                        sensor["name"] = "对接电视相机 B（验证位姿）";
                    if (id == "TV-ARM-WRIST-B" && sensor.value("name").toString() == "机械臂另一腕部相机（验证位姿）")
                        sensor["name"] = "机械臂腕部相机 B（偏置验证位）";
                    if (sensor.value("targetId").toString() == "DOCK-TARGET")
                        sensor["targetId"] = "ACTIVE_TARGET";
                }
                mergedSensors.append(sensor);
            }
            for (const auto &entry : savedSensorArray) {
                const QJsonObject sensor = entry.toObject();
                const QString id = sensor.value("id").toString();
                if (savedSensors.contains(id)) {
                    mergedSensors.append(sensor);
                    savedSensors.remove(id);
                }
            }
            m_config["sensors"] = mergedSensors;
        }
    }
    m_sensorConfigs = m_config.value("sensors").toArray();
    m_solarSlider->setValue(0);
    m_dynamicsSpeed->setValue(m_config.value("dynamicsSpeed").toDouble(1));
    const QJsonObject orbit = m_config.value("orbit").toObject();
    const QStringList keys = {"altitudeKm", "eccentricity", "inclinationDeg", "raanDeg",
                              "argPeriapsisDeg", "meanAnomalyDeg", "timeScale"};
    const QVector<double> defaults = {600, 0.001, 51.6, 0, 0, 0, 60};
    for (int i = 0; i < m_orbitSpins.size(); ++i)
        m_orbitSpins[i]->setValue(orbit.value(keys[i]).toDouble(defaults[i]));
    QDateTime epoch = QDateTime::fromString(orbit.value("epochUtc").toString(), Qt::ISODateWithMs);
    if (!epoch.isValid()) epoch = QDateTime::fromString(orbit.value("epochUtc").toString(), Qt::ISODate);
    if (epoch.isValid()) m_epochUtcEdit->setDateTime(epoch.toUTC());
    for (const auto &entry : m_sensorConfigs) {
        const QJsonObject sensor = entry.toObject();
        const QString id = sensor.value("id").toString();
        if (!id.isEmpty()) m_sensorCombo->addItem(sensor.value("name").toString(id), id);
    }
    if (m_sensorCombo->count()) editSelectedSensor();
}

// 把当前选中传感器的配置字段填入右侧编辑控件。
// 控件编辑期间由标志位避免逐个 setValue 又触发一轮配置回写。
void MainWindow::editSelectedSensor()
{
    const QScopedValueRollback<bool> loading(m_loadingSensorEditor, true);
    const QString id = m_sensorCombo->currentData().toString();
    for (const auto &entry : m_sensorConfigs) {
        const QJsonObject sensor = entry.toObject();
        if (sensor.value("id").toString() != id) continue;
        m_sensorPositionReference->setText(sensor.value("positionReference").toString() == "modelSurface"
            ? "参考模型安装点（XYZ 为米制偏移）" : "旧版安装节点坐标");
        m_fovHorizontal->setValue(sensor.value("horizontalFovDeg").toDouble(32));
        m_fovVertical->setValue(sensor.value("verticalFovDeg").toDouble(32));
        const QJsonArray position = sensor.value("localPositionM").toArray();
        const QJsonArray rotation = sensor.value("localEulerDeg").toArray();
        for (int i = 0; i < 3; ++i) {
            m_sensorPositionSpins[i]->setValue(i < position.size() ? position[i].toDouble() : 0);
            m_sensorRotationSpins[i]->setValue(i < rotation.size() ? rotation[i].toDouble() : 0);
        }
        m_warningEnabled->setChecked(sensor.value("warningEnabled").toBool(true));
        return;
    }
}

// 把编辑控件中的视场、安装偏移、姿态和告警开关写回选中传感器的 JSON 对象。
// 局部位置三个数按 X/Y/Z 顺序存入配置；发送端字段名 localPositionM 表示传给 Unity 的单位是米。
void MainWindow::applySensorEditor()
{
    const QString id = m_sensorCombo->currentData().toString();
    for (int i = 0; i < m_sensorConfigs.size(); ++i) {
        QJsonObject sensor = m_sensorConfigs[i].toObject();
        if (sensor.value("id").toString() != id) continue;
        sensor["horizontalFovDeg"] = m_fovHorizontal->value();
        sensor["verticalFovDeg"] = m_fovVertical->value();
        sensor["localPositionM"] = vectorArray(m_sensorPositionSpins);
        sensor["localEulerDeg"] = vectorArray(m_sensorRotationSpins);
        sensor["warningEnabled"] = m_warningEnabled->isChecked();
        m_sensorConfigs.replace(i, sensor);
        return;
    }
}

// 从界面控件和当前配置拼一份发给 Unity 的状态快照。
QJsonObject MainWindow::currentState() const
{
    QJsonObject state;
    state["dynamicsSpeed"] = m_dynamicsSpeed->value();
    state["activePair"] = QJsonObject{{"target", "CrewVehicle"}, {"chaser", "SpaceStation"}};

    const QStringList keys = {"altitudeKm", "eccentricity", "inclinationDeg", "raanDeg",
                              "argPeriapsisDeg", "meanAnomalyDeg", "timeScale"};
    QJsonObject orbit;
    for (int i = 0; i < keys.size(); ++i) orbit[keys[i]] = m_orbitSpins[i]->value();
    orbit["leapSeconds"] = m_config.value("orbit").toObject().value("leapSeconds").toDouble(37);
    orbit["epochUtc"] = m_epochUtcEdit->dateTime().toUTC().toString(Qt::ISODate);
    state["orbit"] = orbit;
    state["sensors"] = m_sensorConfigs;
    state["viewSensorId"] = QString();
    state["overviewMode"] = m_config.value("overviewMode").toString("fixed");
    state["displaySensorIds"] = m_config.value("displaySensorIds").toArray();
    return state;
}

// 把当前界面状态补齐成 SPR/1 state 消息，压成单行 JSON 后经本机 TCP 发给 Unity。
// 序号自增后显式转成 qint64，避免把较大的无符号 Qt 计数直接传入仅支持有符号整数的 JSON 构造器。
// 结尾换行是双方的消息边界；这不是 TCP 自身提供的边界。
void MainWindow::sendState()
{
    if (!m_handshake || m_socket->state() != QAbstractSocket::ConnectedState) return;
    QJsonObject message = currentState();
    message["showRays"] = m_showRays->isChecked();
    message["raySensorId"] = m_raySensor->currentData().toString();
    message["manualSolarRole"] = m_manualSolarRole->currentData().toString();
    message["manualSolarDiagnostic"] = m_manualSolarDiagnostic->isChecked();
    message["solarArrayDeg"] = m_solarSlider->value();
    message["demoRunId"] = m_demoRunId;
    message["demoDocking"] = m_demoActive;
    message["protocol"] = "SPR/1";
    message["type"] = "state";
    message["seq"] = static_cast<qint64>(++m_sequence);
    message["utc"] = QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs);
    const QByteArray line = QJsonDocument(message).toJson(QJsonDocument::Compact) + '\n';
    m_socket->write(line);
    RuntimeData::appendLog(m_tcpOutLog, line);
}

// 解开 Unity 发来的一行 JSON，根据 type 分派 ready、firstFrame、telemetry、sensorFrame 和视图设置事件。
// ready 时比对实例令牌，令牌相同才标记握手完成并开始发送状态；不匹配则中断连接。
// 传感器帧另交保存流程，遥测另交界面表格；JSON 解析失败或协议不符就丢弃。
void MainWindow::receiveMessage(const QByteArray &line)
{
    QJsonParseError error;
    const QJsonDocument document = QJsonDocument::fromJson(line, &error);
    if (error.error != QJsonParseError::NoError || !document.isObject()) return;
    const QJsonObject message = document.object();
    const QString type = message.value("type").toString();
    if (message.value("protocol").toString() != "SPR/1") return;
    if (type == "ready") {
        if (message.value("instanceToken").toString() != m_instanceToken) {
            startupEvent("tcp-identity-mismatch");
            m_socket->abort();
            return;
        }
        m_handshake = true;
        startupEvent("application-ready");
        if (!m_firstFrame && message.value("firstFrameRendered").toBool()) {
            m_firstFrame = true;
            startupEvent("first-frame", message.value("firstFrameUtc").toString());
        }
        sendState();
        updateStartupStatus();
        return;
    }
    if (!m_handshake) return;
    if (type == "firstFrame" && !m_firstFrame) {
        m_firstFrame = true;
        startupEvent("first-frame", message.value("utc").toString());
        updateStartupStatus();
        return;
    }
    if (type == "telemetry" && !m_firstTelemetry) {
        m_firstTelemetry = true;
        startupEvent("first-telemetry", QString("player=%1×%2")
            .arg(message.value("playerWidth").toInt()).arg(message.value("playerHeight").toInt()));
        updateStartupStatus();
    }
    if (message.value("type").toString() == "sensorFrame") {
        saveSensorFrame(message);
        return;
    }
    RuntimeData::appendLog(m_tcpInLog, line + '\n', 16 * 1024 * 1024);
    if (message.value("type").toString() == "telemetry") showTelemetry(message);
    else if (message.value("type").toString() == "uiEvent"
             && message.value("name").toString() == "viewSettings") {
        m_config["overviewMode"] = message.value("overviewMode").toString("fixed");
        m_config["displaySensorIds"] = message.value("displaySensorIds").toArray();
        // Persist only the two view keys. Editing a view must never snapshot transient controls.
        const QString path = appDataPath("config/user_config.json");
        if (path.isEmpty()) return;
        QJsonObject saved;
        QFile previous(path);
        if (previous.open(QIODevice::ReadOnly)) {
            saved = QJsonDocument::fromJson(previous.readAll()).object();
            previous.close();
        }
        saved["overviewMode"] = m_config.value("overviewMode");
        saved["displaySensorIds"] = m_config.value("displaySensorIds");
        if (RuntimeData::saveJson(path, saved))
            statusBar()->showMessage("Unity 视图设置已保存：" + path, 4000);
    }
    else if (message.value("type").toString() == "error")
        statusBar()->showMessage(message.value("message").toString(), 8000);
}

// 把当前界面设置保存到用户数据目录，而非覆盖程序自带的默认配置。
// QSaveFile 采用临时写入后提交的保存方式；无法取得可写数据目录时提前返回。
void MainWindow::saveConfig()
{
    if (!requireWritableData()) return;
    const QString path = appDataPath("config/user_config.json");
    const QJsonObject state = currentState();
    if (!RuntimeData::saveJson(path, state)) {
        QMessageBox::warning(this, "保存失败", "无法写入参数文件：\n" + QDir(m_dataRoot).filePath("config/user_config.json"));
        return;
    }
    m_config = state;
    statusBar()->showMessage("参数已保存：" + path, 5000);
}
