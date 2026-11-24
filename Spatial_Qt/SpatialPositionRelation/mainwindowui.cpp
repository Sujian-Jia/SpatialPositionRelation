#include "mainwindow.h"
#include "runtimedata.h"
#include "telemetrydisplay.h"
#include <QTimer>
#include <QCheckBox>
#include <QComboBox>
#include <QDateTimeEdit>
#include <QDoubleSpinBox>
#include <QFont>
#include <QFormLayout>
#include <QFrame>
#include <QGridLayout>
#include <QGroupBox>
#include <QHeaderView>
#include <QHBoxLayout>
#include <QLabel>
#include <QPushButton>
#include <QSlider>
#include <QSplitter>
#include <QTabWidget>
#include <QTableWidget>
#include <QTimeZone>
#include <QVBoxLayout>

QDoubleSpinBox *MainWindow::makeSpin(double minimum, double maximum, double step, int decimals)
{
    auto *spin = new QDoubleSpinBox;
    spin->setRange(minimum, maximum);
    spin->setSingleStep(step);
    spin->setDecimals(decimals);
    spin->setKeyboardTracking(true);
    spin->setMinimumWidth(96);
    return spin;
}

void MainWindow::buildUi()
{
    setWindowTitle("空间位置关系计算模块");
    resize(1540, 920);
    setMinimumSize(1180, 720);
    // Reserve the status bar before laying out the viewport; notifications must not resize Unity.
    statusBar();
    auto *central = new QWidget(this);
    auto *root = new QVBoxLayout(central);
    root->setContentsMargins(12, 10, 12, 10);
    root->setSpacing(8);
    auto *header = new QHBoxLayout;
    auto *title = new QLabel("空间位置关系计算模块");
    QFont titleFont = title->font();
    titleFont.setPointSize(15);
    titleFont.setBold(true);
    title->setFont(titleFont);
    m_connectionLabel = new QLabel("Unity 启动中");
    m_connectionLabel->setAlignment(Qt::AlignRight | Qt::AlignVCenter);
    header->addWidget(title);
    header->addStretch(1);
    header->addWidget(m_connectionLabel);
    m_restartUnity = new QPushButton("重试 Unity", central);
    header->addWidget(m_restartUnity);
    m_restartUnity->hide();
    connect(m_restartUnity, &QPushButton::clicked, this, &MainWindow::startUnity);
    auto *startupLogButton = new QPushButton("运行状态", central);
    header->addWidget(startupLogButton);
    connect(startupLogButton, &QPushButton::clicked, this, &MainWindow::showRunStatus);
    root->addLayout(header);

    m_mainSplitter = new QSplitter(Qt::Horizontal, central);
    m_controlPanel = new QWidget(m_mainSplitter);
    m_controlPanel->setMinimumWidth(340);
    m_controlPanel->setMaximumWidth(510);
    auto *controlLayout = new QVBoxLayout(m_controlPanel);
    controlLayout->setContentsMargins(0, 0, 8, 0);
    auto *tabs = new QTabWidget(m_controlPanel);

    auto *motionTab = new QWidget(tabs);
    auto *motionLayout = new QVBoxLayout(motionTab);
    motionLayout->setContentsMargins(8, 8, 8, 8);
    auto *pairForm = new QFormLayout;
    m_targetCraft = new QComboBox(motionTab);
    m_chaserCraft = new QComboBox(motionTab);
    const QList<QPair<QString, QString>> craftChoices = {
        {"空间站（参考模型）", "SpaceStation"}, {"载人飞船（参考模型）", "CrewVehicle"},
        {"货运飞船（参考模型）", "CargoVehicle"}, {"光学仓（验证件）", "OpticalModule"}};
    for (const auto &choice : craftChoices) {
        m_targetCraft->addItem(choice.first, choice.second);
        m_chaserCraft->addItem(choice.first, choice.second);
    }
    m_chaserCraft->setCurrentIndex(1);
    pairForm->addRow("目标航天器", m_targetCraft);
    pairForm->addRow("追踪航天器", m_chaserCraft);
    motionLayout->addLayout(pairForm);

    auto *distanceGroup = new QGroupBox("相对位置");
    auto *distanceLayout = new QVBoxLayout(distanceGroup);
    auto *distanceHeader = new QHBoxLayout;
    distanceHeader->addWidget(new QLabel("对接距离"));
    distanceHeader->addStretch(1);
    m_distanceLabel = new QLabel("20.0 m");
    distanceHeader->addWidget(m_distanceLabel);
    distanceLayout->addLayout(distanceHeader);
    m_distanceSlider = new QSlider(Qt::Horizontal, distanceGroup);
    m_distanceSlider->setRange(1, 1000);
    m_distanceSlider->setPageStep(50);
    distanceLayout->addWidget(m_distanceSlider);
    auto *demoActions = new QHBoxLayout;
    m_demoButton = new QPushButton("对接演示", distanceGroup);
    auto *resetDemo = new QPushButton("复位", distanceGroup);
    demoActions->addWidget(m_demoButton);
    demoActions->addWidget(resetDemo);
    distanceLayout->addLayout(demoActions);
    m_demoStatus = new QLabel("待开始", distanceGroup);
    m_demoStatus->setWordWrap(true);
    distanceLayout->addWidget(m_demoStatus);
    m_showRays = new QCheckBox("显示射线", distanceGroup);
    m_showRays->setToolTip("显示各敏感器从镜头光心发出的视场边界");
    auto *rayRow = new QHBoxLayout;
    rayRow->addWidget(m_showRays);
    m_raySensor = new QComboBox(distanceGroup);
    m_raySensor->addItem("全部机位", "");
    m_raySensor->addItem("CRDS", "CRDS-PORT");
    m_raySensor->addItem("对接相机 A", "TV-DOCK-WIDE");
    m_raySensor->addItem("对接相机 B", "TV-DOCK-NARROW");
    m_raySensor->addItem("腕部相机 A", "TV-ARM-WRIST");
    m_raySensor->addItem("腕部相机 B", "TV-ARM-WRIST-B");
    m_raySensor->addItem("肘部相机", "TV-ARM-ELBOW");
    m_raySensor->setEnabled(false);
    connect(m_showRays, &QCheckBox::toggled, m_raySensor, &QWidget::setEnabled);
    rayRow->addWidget(m_raySensor, 1);
    distanceLayout->addLayout(rayRow);
    connect(m_demoButton, &QPushButton::clicked, this, [this] {
        if (m_demoActive) return;
        m_demoActive = true;
        m_demoPrepared = false;
        m_targetCraft->setCurrentIndex(m_targetCraft->findData("SpaceStation"));
        m_chaserCraft->setCurrentIndex(m_chaserCraft->findData("CrewVehicle"));
        ++m_demoRunId;
        m_demoButton->setEnabled(false);
        m_distanceSlider->setEnabled(false);
        m_targetCraft->setEnabled(false);
        m_chaserCraft->setEnabled(false);
        m_demoStatus->setText("对接演示进行中…");
        sendState();
    });
    connect(resetDemo, &QPushButton::clicked, this, [this] {
        const bool prepareDemo = m_demoActive || m_demoPrepared;
        m_demoActive = false;
        m_demoPrepared = false;
        ++m_demoRunId;
        m_distanceSlider->setEnabled(true);
        m_targetCraft->setEnabled(true);
        m_chaserCraft->setEnabled(true);
        m_distanceSlider->setValue(qRound(m_defaultConfig.value("relativeDistanceM").toDouble(20) * 10));
        const QJsonArray joints = m_defaultConfig.value("jointsDeg").toArray();
        for (int i = 0; i < m_jointSliders.size(); ++i)
            m_jointSliders[i]->setValue(i < joints.size() ? qRound(joints[i].toDouble()) : 0);
        m_demoPrepared = prepareDemo;
        m_demoStatus->setText(prepareDemo ? "已复位到演示起点" : "待开始");
        m_telemetryTable->setRowCount(0);
        m_lastTelemetry.clear();
        updateStartupStatus();
        sendState();
    });
    motionLayout->addWidget(distanceGroup);

    auto *jointGroup = new QGroupBox("机械臂关节角");
    auto *jointLayout = new QGridLayout(jointGroup);
    for (int i = 0; i < 7; ++i) {
        auto *slider = new QSlider(Qt::Horizontal, jointGroup);
        slider->setRange(-180, 180);
        auto *value = new QLabel("0°");
        value->setMinimumWidth(42);
        value->setAlignment(Qt::AlignRight | Qt::AlignVCenter);
        jointLayout->addWidget(new QLabel(QString("J%1").arg(i + 1)), i, 0);
        jointLayout->addWidget(slider, i, 1);
        jointLayout->addWidget(value, i, 2);
        m_jointSliders.append(slider);
        m_jointValueLabels.append(value);
    }
    motionLayout->addWidget(jointGroup);
    auto *wingGroup = new QGroupBox("帆板", motionTab);
    auto *wingLayout = new QVBoxLayout(wingGroup);
    m_wingStatus = new QLabel("自动对日 · 等待数据", wingGroup);
    m_wingStatus->setWordWrap(true);
    wingLayout->addWidget(m_wingStatus);
    m_manualSolarDiagnostic = new QCheckBox("手动检查转轴", wingGroup);
    m_manualSolarDiagnostic->setToolTip("勾选后通过滑块转动所选航天器的参考太阳翼，取消勾选恢复自动对日");
    auto *wingMode = new QHBoxLayout;
    wingMode->addWidget(m_manualSolarDiagnostic);
    m_manualSolarRole = new QComboBox(wingGroup);
    m_manualSolarRole->addItem("目标航天器", "target");
    m_manualSolarRole->addItem("追踪航天器", "chaser");
    m_manualSolarRole->addItem("两器同时", "both");
    m_manualSolarRole->setEnabled(false);
    connect(m_manualSolarDiagnostic, &QCheckBox::toggled, m_manualSolarRole, &QWidget::setEnabled);
    wingMode->addWidget(m_manualSolarRole, 1);
    wingLayout->addLayout(wingMode);
    auto *wingControls = new QWidget(wingGroup);
    auto *wingRow = new QHBoxLayout(wingControls);
    wingRow->setContentsMargins(0, 0, 0, 0);
    wingRow->addWidget(new QLabel("帆板转角"));
    m_solarSlider = new QSlider(Qt::Horizontal, wingControls);
    m_solarSlider->setAccessibleName("帆板转角");
    m_solarSlider->setRange(-90, 90);
    m_solarSlider->setEnabled(false);
    auto *wingValue = new QLabel("0°");
    wingValue->setMinimumWidth(42);
    wingValue->setAlignment(Qt::AlignRight | Qt::AlignVCenter);
    wingRow->addWidget(m_solarSlider, 1);
    wingRow->addWidget(wingValue);
    wingLayout->addWidget(wingControls);
    m_wingControlHint = new QLabel(wingGroup);
    m_wingControlHint->setWordWrap(true);
    wingLayout->addWidget(m_wingControlHint);
    connect(m_manualSolarDiagnostic, &QCheckBox::toggled, this, &MainWindow::updateSolarControls);
    connect(m_manualSolarRole, &QComboBox::currentIndexChanged, this, &MainWindow::updateSolarControls);
    updateSolarControls();
    motionLayout->addWidget(wingGroup);
    connect(m_solarSlider, &QSlider::valueChanged, this, [wingValue](int value) {
        wingValue->setText(QString::number(value) + "°");
    });
    motionLayout->addStretch(1);
    tabs->addTab(motionTab, "位置/机构");

    auto *sensorTab = new QWidget(tabs);
    auto *sensorLayout = new QVBoxLayout(sensorTab);
    sensorLayout->setContentsMargins(8, 8, 8, 8);
    m_sensorCombo = new QComboBox(sensorTab);
    sensorLayout->addWidget(m_sensorCombo);
    auto *sensorForm = new QFormLayout;
    m_fovHorizontal = makeSpin(1, 179, 1, 1);
    m_fovVertical = makeSpin(1, 179, 1, 1);
    sensorForm->addRow("水平全视场 (°)", m_fovHorizontal);
    sensorForm->addRow("垂直全视场 (°)", m_fovVertical);
    m_sensorPositionReference = new QLabel(sensorTab);
    m_sensorPositionReference->setWordWrap(true);
    sensorForm->addRow("位置基准", m_sensorPositionReference);
    const QStringList axes = {"X", "Y", "Z"};
    for (const auto &axis : axes) {
        auto *spin = makeSpin(-20, 20, 0.05, 2);
        m_sensorPositionSpins.append(spin);
        sensorForm->addRow("安装位置 " + axis + " (m)", spin);
    }
    for (const auto &axis : axes) {
        auto *spin = makeSpin(-180, 180, 1, 1);
        m_sensorRotationSpins.append(spin);
        sensorForm->addRow("安装欧拉角 " + axis + " (°)", spin);
    }
    sensorLayout->addLayout(sensorForm);
    m_warningEnabled = new QCheckBox("参与光照告警", sensorTab);
    sensorLayout->addWidget(m_warningEnabled);
    m_sensorSummary = new QLabel("选择传感器查看实时结果");
    m_sensorSummary->setWordWrap(true);
    sensorLayout->addWidget(m_sensorSummary);
    auto *sensorActions = new QGridLayout;
    auto *saveConfigButton = new QPushButton("保存参数", sensorTab);
    auto *saveDataButton = new QPushButton("保存本传感器数据", sensorTab);
    auto *captureButton = new QPushButton("保存本传感器画面", sensorTab);
    m_recordButton = new QPushButton("开始逐帧录制", sensorTab);
    sensorActions->addWidget(saveConfigButton, 0, 0);
    sensorActions->addWidget(saveDataButton, 0, 1);
    sensorActions->addWidget(captureButton, 1, 0);
    sensorActions->addWidget(m_recordButton, 1, 1);
    sensorLayout->addLayout(sensorActions);
    sensorLayout->addStretch(1);
    tabs->addTab(sensorTab, "敏感器");

    auto *orbitTab = new QWidget(tabs);
    auto *orbitLayout = new QVBoxLayout(orbitTab);
    orbitLayout->setContentsMargins(8, 8, 8, 8);
    auto *orbitForm = new QFormLayout;
    const QStringList orbitNames = {"轨道高度 (km)", "偏心率", "倾角 (°)", "升交点赤经 (°)",
                                    "近地点幅角 (°)", "平近点角 (°)", "仿真时间倍率"};
    const QVector<QPair<double, double>> orbitRanges = {{200, 50000}, {0, 0.8}, {-180, 180},
        {-360, 360}, {-360, 360}, {-360, 360}, {0, 10000}};
    for (int i = 0; i < orbitNames.size(); ++i) {
        auto *spin = makeSpin(orbitRanges[i].first, orbitRanges[i].second, i == 1 ? 0.001 : 1,
                              i == 1 ? 5 : 2);
        m_orbitSpins.append(spin);
        orbitForm->addRow(orbitNames[i], spin);
    }
    m_epochUtcEdit = new QDateTimeEdit(orbitTab);
    m_epochUtcEdit->setDisplayFormat("yyyy-MM-dd HH:mm:ss 'UTC'");
    m_epochUtcEdit->setTimeZone(QTimeZone::UTC);
    m_epochUtcEdit->setCalendarPopup(true);
    orbitForm->addRow("轨道历元", m_epochUtcEdit);
    orbitLayout->addWidget(new QLabel("轨道参数（地心惯性系）"));
    orbitLayout->addLayout(orbitForm);
    m_attitudeMode = new QComboBox(orbitTab);
    m_attitudeMode->addItem("欧拉角", "euler");
    m_attitudeMode->addItem("四元数 WXYZ", "quaternion");
    orbitLayout->addWidget(m_attitudeMode);
    auto *attitudeForm = new QFormLayout;
    m_eulerAttitudeEditor = new QWidget(orbitTab);
    auto *eulerLayout = new QFormLayout(m_eulerAttitudeEditor);
    for (const auto &axis : axes) {
        auto *spin = makeSpin(-180, 180, 1, 1);
        m_attitudeSpins.append(spin);
        eulerLayout->addRow("航天器姿态 " + axis + " (°)", spin);
    }
    m_quaternionAttitudeEditor = new QWidget(orbitTab);
    auto *quaternionLayout = new QFormLayout(m_quaternionAttitudeEditor);
    const QStringList quaternionAxes = {"W", "X", "Y", "Z"};
    for (const auto &axis : quaternionAxes) {
        auto *spin = makeSpin(-1, 1, 0.01, 6);
        m_quaternionSpins.append(spin);
        quaternionLayout->addRow("q" + axis, spin);
    }
    attitudeForm->addRow("欧拉角", m_eulerAttitudeEditor);
    attitudeForm->addRow("四元数", m_quaternionAttitudeEditor);
    orbitLayout->addLayout(attitudeForm);
    m_quaternionAttitudeEditor->hide();
    orbitLayout->addStretch(1);
    tabs->addTab(orbitTab, "轨道/姿态");
    controlLayout->addWidget(tabs);
    m_mainSplitter->addWidget(m_controlPanel);

    auto *viewPanel = new QWidget(m_mainSplitter);
    auto *viewLayout = new QVBoxLayout(viewPanel);
    viewLayout->setContentsMargins(4, 0, 0, 0);
    auto *viewHeading = new QHBoxLayout;
    viewHeading->addWidget(new QLabel("Unity 视角"));
    viewHeading->addStretch(1);
    viewHeading->addWidget(new QLabel("布局、相机选择与放大由 Unity 视图内控制"));
    viewLayout->addLayout(viewHeading);
    m_viewport = new QFrame(viewPanel);
    m_viewport->setStyleSheet("QFrame { background:#05090d; border:1px solid #344054; }");
    m_viewport->setAttribute(Qt::WA_NativeWindow);
    m_viewport->installEventFilter(this);
    viewLayout->addWidget(m_viewport, 1);
    m_telemetryTable = new QTableWidget(0, 10, viewPanel);
    m_telemetryTable->setHorizontalHeaderLabels(TelemetryDisplay::headers());
    const auto headerHelp = TelemetryDisplay::headerHelp();
    for (int column = 0; column < headerHelp.size(); ++column)
        m_telemetryTable->horizontalHeaderItem(column)->setToolTip(headerHelp[column]);
    m_telemetryTable->setEditTriggers(QAbstractItemView::NoEditTriggers);
    m_telemetryTable->setSelectionBehavior(QAbstractItemView::SelectRows);
    m_telemetryTable->setAlternatingRowColors(true);
    m_telemetryTable->setStyleSheet("QTableWidget { background:#fff; alternate-background-color:#f5f7fb; color:#1c2834; }"
        "QTableWidget::item:selected { background:#dceaff; }");
    m_telemetryTable->verticalHeader()->hide();
    m_telemetryTable->verticalHeader()->setDefaultSectionSize(23);
    auto *tableHeader = m_telemetryTable->horizontalHeader();
    tableHeader->setSectionResizeMode(QHeaderView::Interactive);
    tableHeader->setStretchLastSection(true);
    const QList<int> columnWidths = {132, 85, 78, 115, 118, 125, 89, 89, 190, 220};
    for (int i = 0; i < columnWidths.size(); ++i) m_telemetryTable->setColumnWidth(i, columnWidths[i]);
    m_telemetryTable->setFixedHeight(176);
    viewLayout->addWidget(m_telemetryTable);
    m_mainSplitter->addWidget(viewPanel);
    m_mainSplitter->setStretchFactor(0, 0);
    m_mainSplitter->setStretchFactor(1, 1);
    m_mainSplitter->setSizes({390, 1120});
    root->addWidget(m_mainSplitter, 1);
    setCentralWidget(central);

    connect(saveConfigButton, &QPushButton::clicked, this, &MainWindow::saveConfig);
    connect(saveDataButton, &QPushButton::clicked, this, &MainWindow::saveSelectedTelemetry);
    connect(captureButton, &QPushButton::clicked, this, &MainWindow::captureFrame);
    connect(m_recordButton, &QPushButton::clicked, this, &MainWindow::toggleFrameRecording);
}

void MainWindow::connectControls()
{
    const auto changed = [this] { if (m_sendTimer) m_sendTimer->start(); };
    const auto resumeManualPosition = [this] {
        if (!m_demoPrepared) return;
        m_demoPrepared = false;
        m_demoStatus->setText("待开始");
    };
    const auto pairChanged = [this, changed, resumeManualPosition] {
        resumeManualPosition();
        updateStartupStatus();
        changed();
    };
    connect(m_targetCraft, qOverload<int>(&QComboBox::currentIndexChanged), this, pairChanged);
    connect(m_chaserCraft, qOverload<int>(&QComboBox::currentIndexChanged), this, pairChanged);
    connect(m_manualSolarDiagnostic, &QCheckBox::toggled, this, &MainWindow::sendState);
    connect(m_manualSolarRole, &QComboBox::currentIndexChanged, this, &MainWindow::sendState);
    connect(m_raySensor, &QComboBox::currentIndexChanged, this, changed);
    connect(m_showRays, &QCheckBox::toggled, this, changed);
    connect(m_distanceSlider, &QSlider::valueChanged, this, [this, changed, resumeManualPosition](int value) {
        resumeManualPosition();
        m_distanceLabel->setText(QString::number(value / 10.0, 'f', 1) + " m");
        changed();
    });
    for (int i = 0; i < m_jointSliders.size(); ++i) {
        connect(m_jointSliders[i], &QSlider::valueChanged, this, [this, i, changed](int value) {
            m_jointValueLabels[i]->setText(QString::number(value) + "°");
            changed();
        });
    }
    connect(m_solarSlider, &QSlider::valueChanged, this, changed);
    connect(m_solarSlider, &QSlider::sliderReleased, this, [this] {
        m_sendTimer->stop();
        sendState();
    });
    for (auto *spin : m_orbitSpins)
        connect(spin, qOverload<double>(&QDoubleSpinBox::valueChanged), this, changed);
    connect(m_epochUtcEdit, &QDateTimeEdit::dateTimeChanged, this, changed);
    connect(m_attitudeMode, qOverload<int>(&QComboBox::currentIndexChanged), this, [this, changed](int) {
        const bool eulerSelected = m_attitudeMode->currentData().toString() == "euler";
        m_eulerAttitudeEditor->setVisible(eulerSelected);
        m_quaternionAttitudeEditor->setVisible(!eulerSelected);
        changed();
    });
    for (auto *spin : m_attitudeSpins)
        connect(spin, qOverload<double>(&QDoubleSpinBox::valueChanged), this, changed);
    for (auto *spin : m_quaternionSpins)
        connect(spin, qOverload<double>(&QDoubleSpinBox::valueChanged), this, changed);
    connect(m_sensorCombo, qOverload<int>(&QComboBox::currentIndexChanged), this, [this, changed] {
        editSelectedSensor();
        changed();
    });
    const auto sensorChanged = [this, changed] {
        if (m_loadingSensorEditor) return;
        applySensorEditor();
        changed();
    };
    connect(m_fovHorizontal, qOverload<double>(&QDoubleSpinBox::valueChanged), this, sensorChanged);
    connect(m_fovVertical, qOverload<double>(&QDoubleSpinBox::valueChanged), this, sensorChanged);
    for (auto *spin : m_sensorPositionSpins)
        connect(spin, qOverload<double>(&QDoubleSpinBox::valueChanged), this, sensorChanged);
    for (auto *spin : m_sensorRotationSpins)
        connect(spin, qOverload<double>(&QDoubleSpinBox::valueChanged), this, sensorChanged);
    connect(m_warningEnabled, &QCheckBox::toggled, this, sensorChanged);
}

void MainWindow::showTelemetry(const QJsonObject &message)
{
    const QJsonArray sensors = message.value("sensors").toArray();
    if (message.value("demoRunId").toInt() != m_demoRunId) return;
    m_telemetryTable->setRowCount(sensors.size());
    int row = 0;
    bool anyWarning = false;
    const double demoTime = message.value("demoTimeSec").toDouble();
    if (m_demoActive && message.value("demoDocking").toBool()) {
        if (message.value("demoFinished").toBool()) {
            m_demoStatus->setText(QString("%1 · 位置误差 %2 m · 轴向误差 %3°")
                .arg(message.value("dockingAligned").toBool() ? "已对准并停稳" : "已到终点，误差超限")
                .arg(message.value("dockingPositionErrorM").toDouble(), 0, 'f', 3)
                .arg(message.value("dockingAngleErrorDeg").toDouble(), 0, 'f', 2));
        } else {
            m_demoStatus->setText(QString("正在接近 · 仿真 %1 s").arg(demoTime, 0, 'f', 1));
        }
    }
    const auto describeWings = [&](const QString &role, bool &hasWings) {
        QStringList values;
        for (const auto &entry : message.value("wings").toArray()) {
            const auto wing = entry.toObject();
            if (wing.value("role").toString() != role) continue;
            const auto mode = wing.value("manual").toBool() ? "手动" : "自动";
            values.append(QString("%1 %2°").arg(wing.value("hinge").toString())
                         .arg(wing.value("angleDeg").toDouble(), 0, 'f', 1));
            if (values.size() == 1) values.prepend(mode);
        }
        hasWings = !values.isEmpty();
        return hasWings ? values.join(" · ") : QString("参考模型无已绑定的可动翼");
    };
    const QString targetWings = describeWings("target", m_targetHasWings);
    const QString chaserWings = describeWings("chaser", m_chaserHasWings);
    m_wingStatus->setText(QString("目标器 %1\n追踪器 %2").arg(targetWings, chaserWings));
    updateSolarControls();
    for (const auto &entry : sensors) {
        const QJsonObject sensor = entry.toObject();
        const QString id = sensor.value("id").toString("unknown");
        m_lastTelemetry.insert(id, sensor);
        anyWarning = anyWarning || sensor.value("warning").toBool();
        QString sensorName = id;
        for (const auto &configEntry : m_sensorConfigs) {
            const QJsonObject config = configEntry.toObject();
            if (config.value("id").toString() == id) {
                sensorName = config.value("name").toString(id);
                break;
            }
        }
        const QString rawStatus = sensor.value("status").toString();
        const bool blocked = rawStatus == "FullyBlocked" || rawStatus == "PartiallyBlocked";
        const bool future = sensor.value("futureOcclusion").toBool() && m_demoActive;
        const QStringList fields = TelemetryDisplay::row(sensor, sensorName, message.value("chaserCraftId").toString(), m_demoActive);
        const QString illuminationHelp = TelemetryDisplay::illuminationHelp(sensor);
        for (int column = 0; column < fields.size(); ++column) {
            auto *item = m_telemetryTable->item(row, column);
            if (!item) {
                item = new QTableWidgetItem;
                m_telemetryTable->setItem(row, column, item);
            }
            item->setText(fields[column]);
            item->setToolTip(column == 7 && !illuminationHelp.isEmpty() ? fields[column] + "% · " + illuminationHelp : fields[column]);
            item->setForeground(QColor(column == 2 && blocked ? "#9e2018" : column == 3 && future ? "#795000" : "#1c2834"));
        }
        ++row;
    }
    m_sensorSummary->setText(QString("仿真时间 %1 s；%2 个传感器%3")
                                 .arg(message.value("simTimeSec").toDouble(), 0, 'f', 1)
                                 .arg(sensors.size())
                                 .arg(anyWarning ? "；存在光照告警" : ""));
    m_sensorSummary->setStyleSheet(anyWarning ? "color:#b42318;font-weight:600;" : "color:#16845b;");
}

void MainWindow::updateSolarControls()
{
    const QString role = m_manualSolarRole->currentData().toString();
    const bool canRotate = (role != "chaser" && m_targetHasWings) || (role != "target" && m_chaserHasWings);
    const bool manual = m_manualSolarDiagnostic->isChecked();
    m_solarSlider->setEnabled(manual && canRotate);
    QString hint = "自动对日中。勾选「手动检查转轴」可使用滑块。";
    if (manual && !m_firstTelemetry)
        hint = "正在等待可动翼数据。";
    else if (manual && !canRotate)
        hint = "所选模型无已绑定的可动翼。请选择带可动翼的航天器。";
    else if (manual && role == "both" && (!m_targetHasWings || !m_chaserHasWings))
        hint = "滑块仅驱动已绑定的翼，沿各翼长边转动。";
    else if (manual)
        hint = "沿各翼长边转动。范围为演示限位 ±90°。";
    if (m_wingControlHint->text() != hint) m_wingControlHint->setText(hint);
}
