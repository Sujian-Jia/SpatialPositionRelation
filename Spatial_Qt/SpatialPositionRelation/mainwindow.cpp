#include "mainwindow.h"
#include "runtimedata.h"
#include <QCoreApplication>
#include <QHostAddress>
#include <QLabel>
#include <QLockFile>
#include <QProcess>
#include <QPushButton>
#include <QStatusBar>
#include <QTcpSocket>
#include <QTimer>

#ifdef Q_OS_WIN
#include <windows.h>
#endif

// 构造 Qt 主窗口，读取数据和配置，连接 TCP、Unity 子进程、定时器与界面控件。
// 这是 C++ 构造函数；冒号后的初始化列表先初始化 QMainWindow 父类。
// connect 把信号（事件）接到槽（响应函数或 lambda）；定时器到期时由 Qt 事件循环调用对应工作。
MainWindow::MainWindow(const QString &dataRoot, QWidget *parent) : QMainWindow(parent)
{
    QCoreApplication::setOrganizationName("SpatialPositionRelation");
    QCoreApplication::setApplicationName("SpatialPositionRelation");
    m_startupClock.start();
    prepareDataSession(dataRoot);
    startupEvent("qt-created", QCoreApplication::applicationFilePath());
    buildUi();
    m_dataStatus = new QLabel(this);
    statusBar()->addPermanentWidget(m_dataStatus);
    updateDataStatus();
    loadConfig();

    m_socket = new QTcpSocket(this);
    connect(m_socket, &QTcpSocket::connected, this, [this] {
        startupEvent("tcp-connected", QString::number(m_unityPort));
        updateStartupStatus();
    });
    connect(m_socket, &QTcpSocket::disconnected, this, [this] {
        m_receiveBuffer.clear();
        m_pendingFrameSequence = 0;
        m_handshake = false;
        if (m_frameRecording) toggleFrameRecording();
        if (!m_closing) startupEvent("tcp-disconnected");
        updateStartupStatus();
    });
    connect(m_socket, &QTcpSocket::readyRead, this, [this] {
        m_receiveBuffer.append(m_socket->readAll());
        while (true) {
            const qsizetype newline = m_receiveBuffer.indexOf('\n');
            if (newline < 0) break;
            const QByteArray line = m_receiveBuffer.left(newline).trimmed();
            m_receiveBuffer.remove(0, newline + 1);
            if (!line.isEmpty()) receiveMessage(line);
        }
    });
    m_unityProcess = new QProcess(this);
    m_unityProcess->setProcessChannelMode(QProcess::MergedChannels);
    connect(m_unityProcess, &QProcess::readyRead, this, [this] { appendProcessLog(m_unityProcess->readAll()); });
    connect(m_unityProcess, &QProcess::started, this, [this] {
        startupEvent("unity-started", QString::number(m_unityProcess->processId()));
        if (m_closing) { m_unityProcess->kill(); return; }
#ifdef Q_OS_WIN
        HANDLE job = CreateJobObjectW(nullptr, nullptr);
        HANDLE process = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE, FALSE,
                                     static_cast<DWORD>(m_unityProcess->processId()));
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits {};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (job && process && SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))
            && AssignProcessToJobObject(job, process)) {
            m_job = reinterpret_cast<quintptr>(job);
            startupEvent("unity-job-assigned");
        } else {
            startupEvent("unity-job-unavailable", QString::number(GetLastError()));
            if (job) CloseHandle(job);
        }
        if (process) CloseHandle(process);
#endif
    });
    connect(m_unityProcess, &QProcess::errorOccurred, this, [this](QProcess::ProcessError error) {
        startupEvent("unity-process-error", m_unityProcess->errorString());
        if (!m_closing && error == QProcess::FailedToStart) startupFailure(m_unityProcess->errorString());
        if (m_closing && m_unityProcess->state() == QProcess::NotRunning) finishUnityCleanup();
    });
    connect(m_unityProcess, qOverload<int, QProcess::ExitStatus>(&QProcess::finished), this,
            [this](int code, QProcess::ExitStatus status) {
        startupEvent("unity-exited", QString("code=%1 status=%2").arg(code).arg(int(status)));
#ifdef Q_OS_WIN
        if (m_job) CloseHandle(reinterpret_cast<HANDLE>(m_job));
        m_job = 0;
#endif
        m_socket->abort();
        m_retiredSessionLock.reset();
        if (m_rotatePlayer) RuntimeData::rotateLog(m_playerLog);
        m_rotatePlayer = false;
        if (m_closing) finishUnityCleanup();
        else if (m_relaunchUnity) {
            m_relaunchUnity = false;
            QTimer::singleShot(0, this, &MainWindow::startUnity);
        }
        else if (m_startupFailed) m_restartUnity->show();
        else startupFailure("Unity 已退出，请重试");
    });

    m_connectTimer = new QTimer(this);
    m_connectTimer->setInterval(800);
    connect(m_connectTimer, &QTimer::timeout, this, &MainWindow::tryConnectUnity);
    m_sendTimer = new QTimer(this);
    m_sendTimer->setSingleShot(true);
    m_sendTimer->setInterval(70);
    connect(m_sendTimer, &QTimer::timeout, this, &MainWindow::sendState);
    m_frameTimer = new QTimer(this);
    m_frameTimer->setInterval(250);
    connect(m_frameTimer, &QTimer::timeout, this, &MainWindow::recordFrame);
    m_viewportSyncTimer = new QTimer(this);
    m_viewportSyncTimer->setInterval(160);
    connect(m_viewportSyncTimer, &QTimer::timeout, this, &MainWindow::resizeUnityChild);
    connectControls();
    auto *logGuard = new QTimer(this);
    logGuard->setInterval(1000);
    connect(logGuard, &QTimer::timeout, this, &MainWindow::maintainLogs);
    logGuard->start();
    QTimer::singleShot(0, this, [this] {
        startupEvent("qt-event-loop-ready");
        startUnity();
    });
}

MainWindow::~MainWindow() = default;
