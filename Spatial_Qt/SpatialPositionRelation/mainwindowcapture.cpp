#include "mainwindow.h"
#include "runtimedata.h"
#include <QComboBox>
#include <QDir>
#include <QDateTime>
#include <QFileInfo>
#include <QJsonDocument>
#include <QLabel>
#include <QPushButton>
#include <QRegularExpression>
#include <QSaveFile>
#include <QStatusBar>
#include <QTcpSocket>
#include <QTimer>

void MainWindow::saveSelectedTelemetry()
{
    if (!requireWritableData()) return;
    const QString id = m_sensorCombo->currentData().toString();
    if (id.isEmpty() || !m_lastTelemetry.contains(id)) {
        statusBar()->showMessage("尚无所选传感器的遥测数据", 4000);
        return;
    }
    const QString relative = "output/sensors/" + safeFileName(id) + "/telemetry_" + RuntimeData::utcFileTimestamp() + ".json";
    const QString path = appDataPath(relative);
    if (RuntimeData::saveJson(path, m_lastTelemetry.value(id)))
        statusBar()->showMessage("传感器数据已保存：" + path, 5000);
    else
        statusBar()->showMessage("无法保存传感器数据：" + QDir(m_dataRoot).filePath(relative), 5000);
}

void MainWindow::captureFrame()
{
    if (!requireWritableData()) return;
    requestSensorFrame(m_sensorCombo->currentData().toString(), false);
}

void MainWindow::recordFrame()
{
    if (m_frameRecording && !m_pendingFrameSequence)
        requestSensorFrame(m_recordingSensorId, true);
}

void MainWindow::requestSensorFrame(const QString &id, bool recording)
{
    if (!m_socket || m_socket->state() != QAbstractSocket::ConnectedState || id.isEmpty()) {
        statusBar()->showMessage("请连接 Unity 并选择传感器", 4000);
        return;
    }
    if (m_pendingFrameSequence) {
        statusBar()->showMessage("正在保存上一帧画面", 4000);
        return;
    }
    const quint64 sequence = ++m_sequence;
    const QJsonObject request{{"protocol", "SPR/1"}, {"type", "captureSensor"},
                              {"seq", static_cast<qint64>(sequence)}, {"sensorId", id},
                              {"utc", QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs)}};
    const QByteArray line = QJsonDocument(request).toJson(QJsonDocument::Compact) + '\n';
    if (m_socket->write(line) != line.size()) {
        statusBar()->showMessage("发送画面请求失败", 5000);
        return;
    }
    m_pendingFrameSequence = sequence;
    m_pendingFrameSensor = id;
    m_pendingFrameRecording = recording;
    RuntimeData::appendLog(m_tcpOutLog, line);
    QTimer::singleShot(3000, this, [this, sequence] {
        if (m_pendingFrameSequence != sequence) return;
        m_pendingFrameSequence = 0;
        if (m_frameRecording) toggleFrameRecording();
        statusBar()->showMessage("等待传感器画面超时", 5000);
    });
}

void MainWindow::saveSensorFrame(const QJsonObject &message)
{
    if (message.value("protocol").toString() != "SPR/1" || !m_pendingFrameSequence
        || message.value("seq").toInteger() != static_cast<qint64>(m_pendingFrameSequence)
        || message.value("sensorId").toString() != m_pendingFrameSensor) return;
    m_pendingFrameSequence = 0;
    const bool recording = m_pendingFrameRecording;
    if (recording && !m_frameRecording) return;
    const QString error = message.value("error").toString();
    const QByteArray png = QByteArray::fromBase64(message.value("pngBase64").toString().toLatin1());
    if (!error.isEmpty() || !png.startsWith("\x89PNG\r\n\x1a\n")) {
        if (recording) toggleFrameRecording();
        statusBar()->showMessage(error.isEmpty() ? QString("Unity 返回的画面无效") : error, 5000);
        return;
    }
    const quint64 index = m_frameIndex + 1;
    const QString name = recording ? QString("frame_%1.png").arg(index, 6, 10, QChar('0'))
                                   : "frame_" + RuntimeData::utcFileTimestamp() + ".png";
    const QString relative = recording ? m_recordingDirectory + "/" + name
        : "output/sensors/" + safeFileName(m_pendingFrameSensor) + "/images/" + name;
    const QString path = appDataPath(relative);
    QSaveFile output(path);
    if (path.isEmpty() || !output.open(QIODevice::WriteOnly)
        || output.write(png) != png.size() || !output.commit()) {
        if (recording) toggleFrameRecording();
        statusBar()->showMessage("无法保存相机画面", 5000);
        return;
    }
    if (!recording) {
        statusBar()->showMessage("传感器画面已保存：" + path, 5000);
        return;
    }
    m_frameIndex = index;
    QJsonObject sample = m_lastTelemetry.value(m_pendingFrameSensor);
    sample["id"] = m_pendingFrameSensor;
    sample["frame"] = name;
    sample["frameUtc"] = message.value("utc").toString();
    const QString telemetryPath = appDataPath(m_recordingDirectory + "/telemetry.jsonl");
    QFile data(telemetryPath);
    const QByteArray line = QJsonDocument(sample).toJson(QJsonDocument::Compact) + '\n';
    if (!data.open(QIODevice::WriteOnly | QIODevice::Append) || data.write(line) != line.size())
        statusBar()->showMessage("图像已保存，遥测写入失败", 5000);
}

void MainWindow::toggleFrameRecording()
{
    if (!m_frameRecording) {
        if (!requireWritableData()) return;
        if (m_socket->state() != QAbstractSocket::ConnectedState) {
            statusBar()->showMessage("请连接 Unity 后录制", 4000);
            return;
        }
        m_recordingSensorId = m_sensorCombo->currentData().toString();
        if (m_recordingSensorId.isEmpty()) {
            statusBar()->showMessage("请先选择要录制的传感器", 4000);
            return;
        }
        m_recordingDirectory = "output/sensors/" + safeFileName(m_recordingSensorId) + "/recording_" + RuntimeData::utcFileTimestamp();
        const QString firstFrame = appDataPath(m_recordingDirectory + "/frame_000001.png");
        const QString directory = firstFrame.isEmpty() ? QString() : QFileInfo(firstFrame).absolutePath();
        if (directory.isEmpty()) {
            statusBar()->showMessage("无法创建录制目录：" + QDir(m_dataRoot).filePath(m_recordingDirectory), 5000);
            return;
        }
        m_frameRecording = true;
        m_frameIndex = 0;
        m_sensorCombo->setEnabled(false);
        m_recordButton->setText("停止逐帧录制");
        m_frameTimer->start();
        statusBar()->showMessage("已开始逐帧录制：" + directory, 5000);
    } else {
        m_frameRecording = false;
        m_frameTimer->stop();
        m_sensorCombo->setEnabled(true);
        m_recordButton->setText("开始逐帧录制");
        statusBar()->showMessage(QString("逐帧录制结束，共 %1 帧").arg(m_frameIndex), 5000);
        if (m_pendingFrameRecording) m_pendingFrameSequence = 0;
        m_recordingSensorId.clear();
    }
}

QString MainWindow::safeFileName(const QString &value) const
{
    QString safe = value;
    safe.replace(QRegularExpression("[^A-Za-z0-9_-]"), "_");
    return safe.isEmpty() ? "sensor" : safe;
}
