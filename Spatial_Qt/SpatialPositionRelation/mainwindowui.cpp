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
#include <QSignalBlocker>
#include <cmath>
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

// 创建 Qt 控制面板、参数控件、Unity 原生窗口容器和遥测表格。
// 控件信号由 connectControls 绑定；这里的视口让 Player 可以作为子窗口显示，而不是用 Qt 重画 3D 场景。
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
    pairForm->addRow("己方（机械臂与敏感器）", new QLabel("空间站 · 参考模型"));
    pairForm->addRow("目标（初段主动接近）", new QLabel("来访器 · 参考模型"));
    motionLayout->addLayout(pairForm);

    auto *distanceGroup = new QGroupBox("相对位置");
    auto *distanceLayout = new QVBoxLayout(distanceGroup);
    auto *distances = new QGridLayout;
    m_distanceLabel = new QLabel("—");
    m_centerDistanceLabel = new QLabel("—");
    m_axialGapLabel = new QLabel("—");
    m_toolDistanceLabel = new QLabel("—");
    distances->addWidget(new QLabel("端口间距"), 0, 0);
    distances->addWidget(m_distanceLabel, 0, 1);
    distances->addWidget(new QLabel("质心间距"), 0, 2);
    distances->addWidget(m_centerDistanceLabel, 0, 3);
    distances->addWidget(new QLabel("轴向间隙"), 1, 0);
    distances->addWidget(m_axialGapLabel, 1, 1);
    distances->addWidget(new QLabel("工具距抓捕点"), 1, 2);
    distances->addWidget(m_toolDistanceLabel, 1, 3);
    distanceLayout->addLayout(distances);
    auto *demoActions = new QHBoxLayout;
    m_demoButton = new QPushButton("播放", distanceGroup);
    auto *resetDemo = new QPushButton("复位", distanceGroup);
    demoActions->addWidget(m_demoButton);
    demoActions->addWidget(resetDemo);
    m_dynamicsSpeed = makeSpin(0.25, 16, 0.25, 2);
    m_dynamicsSpeed->setValue(1);
    m_dynamicsSpeed->setSuffix("×");
    m_dynamicsSpeed->setMinimumWidth(64);
    m_dynamicsSpeed->setToolTip("动力学播放倍率，和轨道日照倍率独立");
    demoActions->addWidget(new QLabel("倍率"));
    demoActions->addWidget(m_dynamicsSpeed);
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
    m_showSunVector = new QCheckBox("显示太阳矢量方向", distanceGroup);
    distanceLayout->addWidget(m_showSunVector);
    connect(m_demoButton, &QPushButton::clicked, this, [this] {
        if (m_demoFinished) {
            ++m_demoRunId;
            m_dynamicsEventsShown = 0;
            m_demoFinished = false;
            m_nativeJointDegrees = {};
        }
        m_demoActive = !m_demoActive;
        m_demoStarted = true;
        clearJointPreview();
        m_demoButton->setText(m_demoActive ? "暂停" : "继续");
        startupEvent(m_demoActive ? "dynamics-play" : "dynamics-pause");
        sendState();
    });
    connect(resetDemo, &QPushButton::clicked, this, [this] {
        m_demoActive = false;
        m_demoFinished = false;
        m_demoStarted = false;
        m_nativeJointDegrees = {};
        clearJointPreview();
        ++m_demoRunId;
        m_dynamicsEventsShown = 0;
        startupEvent("dynamics-reset");
        m_demoButton->setText("播放");
        m_demoStatus->setText("正在复位到工况起点…");
        m_telemetryTable->setRowCount(0);
        m_lastTelemetry.clear();
        sendState();
    });
    motionLayout->addWidget(distanceGroup);

    auto *jointGroup = new QGroupBox("机械臂关节角");
    auto *jointLayout = new QGridLayout(jointGroup);
    m_testJoints = new QCheckBox("测试关节角", jointGroup);
    m_testJoints->setToolTip("仅在待播放或播放完成时可操作。测试只改变总览中的机械臂画面。");
    jointLayout->addWidget(m_testJoints, 0, 0, 1, 3);
    for (int i = 0; i < 7; ++i) {
        const QString name = QString("J%1").arg(i + 1);
        auto *slider = new QSlider(Qt::Horizontal, jointGroup);
        slider->setAccessibleName(name + " 角度滑块");
        slider->setRange(-1800, 1800);
        slider->setPageStep(100);
        auto *value = makeSpin(-180, 180, 0.1, 1);
        value->setAccessibleName(name + " 关节角");
        value->setSuffix("°");
        value->setReadOnly(true);
        value->setButtonSymbols(QAbstractSpinBox::NoButtons);
        value->setMinimumWidth(65);
        jointLayout->addWidget(new QLabel(name), i + 1, 0);
        jointLayout->addWidget(slider, i + 1, 1);
        jointLayout->addWidget(value, i + 1, 2);
        m_jointSliders.append(slider);
        m_jointValues.append(value);
        connect(slider, &QSlider::valueChanged, this, [this, value](int ticks) {
            value->setValue(ticks / 10.0);
            if (m_testJoints->isChecked()) sendPresentation();
        });
    }
    updateJointControls();
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
    m_manualSolarRole->addItem("己方航天器", "chaser");
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
    orbitLayout->addWidget(new QLabel("两器惯性姿态（Unity 坐标，四元数 WXYZ）"));
    auto *attitudes = new QFormLayout;
    m_stationAttitude = new QLabel("—");
    m_visitorAttitude = new QLabel("—");
    m_stationAttitude->setWordWrap(true);
    m_visitorAttitude->setWordWrap(true);
    attitudes->addRow("己方", m_stationAttitude);
    attitudes->addRow("目标", m_visitorAttitude);
    orbitLayout->addLayout(attitudes);
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

// 把滑块、下拉框和复选框变化连接到状态发送或本地编辑响应。
// 70 毫秒单次定时器合并快速连续的控件变化，拖动滑块释放时则立即发送最终值。
void MainWindow::connectControls()
{
    const auto changed = [this] { if (m_sendTimer) m_sendTimer->start(); };
    connect(m_dynamicsSpeed, qOverload<double>(&QDoubleSpinBox::valueChanged), this, changed);
    connect(m_manualSolarDiagnostic, &QCheckBox::toggled, this, &MainWindow::sendState);
    connect(m_manualSolarRole, &QComboBox::currentIndexChanged, this, &MainWindow::sendState);
    connect(m_raySensor, &QComboBox::currentIndexChanged, this, changed);
    connect(m_showRays, &QCheckBox::toggled, this, changed);
    connect(m_showSunVector, &QCheckBox::toggled, this, &MainWindow::sendPresentation);
    connect(m_testJoints, &QCheckBox::toggled, this, [this] {
        displayJointAngles();
        updateJointControls();
        sendPresentation();
    });
    connect(m_solarSlider, &QSlider::valueChanged, this, changed);
    connect(m_solarSlider, &QSlider::sliderReleased, this, [this] {
        m_sendTimer->stop();
        sendState();
    });
    for (auto *spin : m_orbitSpins)
        connect(spin, qOverload<double>(&QDoubleSpinBox::valueChanged), this, changed);
    connect(m_epochUtcEdit, &QDateTimeEdit::dateTimeChanged, this, changed);
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

void MainWindow::updateJointControls()
{
    const bool available = m_nativeJointDegrees.size() == 7 && (!m_demoStarted || m_demoFinished);
    m_testJoints->setEnabled(available);
    for (auto *slider : m_jointSliders) slider->setEnabled(available && m_testJoints->isChecked());
}

void MainWindow::displayJointAngles()
{
    for (int i = 0; i < m_jointSliders.size(); ++i) {
        auto *slider = m_jointSliders[i];
        const QSignalBlocker block(slider);
        const bool hasSample = i < m_nativeJointDegrees.size();
        auto *value = m_jointValues[i];
        value->setSpecialValueText(hasSample ? QString() : QStringLiteral("—"));
        const int ticks = hasSample ? qRound(m_nativeJointDegrees[i].toDouble() * 10.0) : -1800;
        // Preserve the full native reading, including samples outside the nominal preview span.
        slider->setRange(qMin(-1800, ticks), qMax(1800, ticks));
        value->setRange(slider->minimum() / 10.0, slider->maximum() / 10.0);
        slider->setValue(ticks);
        value->setValue(ticks / 10.0);
    }
}

void MainWindow::clearJointPreview()
{
    const QSignalBlocker block(m_testJoints);
    m_testJoints->setChecked(false);
    displayJointAngles();
    updateJointControls();
}

// 把 Unity 的 telemetry JSON 转成 Qt 表格文字、颜色和悬停提示，不在这里重新做空间射线计算。
// 只显示当前 demoRunId 对应的轮次；可见率使用可见点数/总目标点数，未来遮挡预警则只在演示时标出。
// 显示换算交给 TelemetryDisplay::row；当前光照比例在那里乘 100 变百分数，底层 JSON 仍保存 0 到 1 的比例。
void MainWindow::showTelemetry(const QJsonObject &message)
{
    const QJsonArray sensors = message.value("sensors").toArray();
    if (message.value("demoRunId").toInt() != m_demoRunId) return;
    m_telemetryTable->setRowCount(sensors.size());
    int row = 0;
    bool anyWarning = false;
    const QJsonObject native = message.value("dynamics").toObject();
    if (native.value("jointAnglesRad").toArray().size() == 7) {
        const auto meters = [&native](const QString &key) {
            return QString::number(native.value(key).toDouble(), 'f', 3) + " m";
        };
        m_distanceLabel->setText(meters("portDistanceM"));
        m_centerDistanceLabel->setText(meters("centerDistanceM"));
        m_axialGapLabel->setText(meters("axialGapM"));
        m_toolDistanceLabel->setText(meters("toolDistanceM"));
        m_nativeJointDegrees = message.value("jointsDeg").toArray();
        if (!m_testJoints->isChecked()) displayJointAngles();
        updateJointControls();
        const auto attitudeText = [&native](const QString &name) {
            const auto rotation = native.value(name).toObject().value("rotation").toObject();
            QStringList values;
            for (const auto &axis : {"w", "x", "y", "z"})
                values.append(QString::number(rotation.value(axis).toDouble(), 'f', 5));
            return values.join("  ");
        };
        m_stationAttitude->setText(attitudeText("stationCenter"));
        m_visitorAttitude->setText(attitudeText("visitorCenter"));
        const int mode = native.value("mode").toInt();
        const auto events = native.value("eventTimesSec").toArray();
        const QStringList eventNames = {"工具接触", "机械臂抓捕", "对接口二次接触", "已发出锁紧请求", "端口锁紧", "已分离"};
        while (m_dynamicsEventsShown < events.size() && m_dynamicsEventsShown < eventNames.size()) {
            const double eventTime = events[m_dynamicsEventsShown].toDouble(-1);
            if (eventTime < 0) break;
            startupEvent("dynamics-event", QString("%1（仿真 %2 s）")
                .arg(eventNames[m_dynamicsEventsShown]).arg(eventTime, 0, 'f', 3));
            ++m_dynamicsEventsShown;
        }
        QString phase;
        switch (mode) {
        case 0: phase = "目标主动接近"; break;
        case 1: phase = "工具接触"; break;
        case 2: phase = "端口锁紧"; break;
        case 3: phase = events.size() == 6 && events[2].toDouble() >= 0 ? "二次碰撞 · 端口接触" : "己方机械臂抓捕搬运"; break;
        case 4: phase = "已分离"; break;
        default: phase = "等待工况"; break;
        }
        const double demoTime = native.value("timeSec").toDouble();
        const QString status = message.value("demoFinished").toBool() ? "播放完成"
            : m_demoActive ? "播放中" : demoTime > 0 ? "已暂停" : "待播放";
        m_demoStatus->setText(QString("%1 · %2 · %3 s").arg(status, phase).arg(demoTime, 0, 'f', 2));
        if (message.value("demoFinished").toBool() && !m_demoFinished) {
            m_demoFinished = true;
            m_demoActive = false;
            m_demoButton->setText("重播");
            startupEvent("dynamics-finished");
            updateJointControls();
            sendState();
        }
    } else if (!message.value("dynamicsError").toString().isEmpty()) {
        m_demoStatus->setText(message.value("dynamicsError").toString());
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
    m_wingStatus->setText(QString("目标器 %1\n己方 %2").arg(targetWings, chaserWings));
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
        const bool future = sensor.value("futureOcclusion").toBool();
        const QStringList fields = TelemetryDisplay::row(sensor, sensorName, message.value("chaserCraftId").toString(), m_demoActive || sensor.value("trajectoryForecastAvailable").toBool());
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

// 根据 Unity 回来的翼信息启用或解释手动太阳翼滑块。
// 这是 Qt 界面状态更新，不负责算自动对日角度；自动角度在 Unity 的 SpatialRuntime 中更新。
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
