#include "mainwindow.h"
#include "runtimedata.h"
#include <QCloseEvent>
#include <QComboBox>
#include <QCoreApplication>
#include <QDateTime>
#include <QDesktopServices>
#include <QDir>
#include <QDialog>
#include <QDialogButtonBox>
#include <QEvent>
#include <QFile>
#include <QFileDialog>
#include <QFileInfo>
#include <QHostAddress>
#include <QLabel>
#include <QLockFile>
#include <QMessageBox>
#include <QPlainTextEdit>
#include <QProcess>
#include <QProcessEnvironment>
#include <QPushButton>
#include <QStatusBar>
#include <QTcpServer>
#include <QTcpSocket>
#include <QTimer>
#include <QUuid>
#include <QUrl>
#include <QVBoxLayout>
#include <QWidget>

#ifdef Q_OS_WIN
#include <cwchar>
#include <windows.h>
#endif

// 由 Qt 启动 Unity 可执行程序，并通过启动参数传端口、实例令牌和要嵌入的父窗口句柄。
// Qt 用 QProcess 创建独立进程（独立程序实例）；本地临时端口由 QTcpServer 向系统申请。
// HWND/parentHWND 是 Windows 窗口句柄标识；它让 Player 作为子窗口嵌入 Qt 视口，不是图像复制。
// 端口和一次性 instanceToken 随 -spatialPort、-spatialToken 传递，供 TCP 连接与握手时核对。
void MainWindow::startUnity()
{
    if (m_closing || m_unityProcess->state() != QProcess::NotRunning) return;
    m_startupClock.restart();
    m_startupFailed = m_handshake = m_firstFrame = m_firstTelemetry = false;
    m_restartUnity->hide();
    const QString appDir = QCoreApplication::applicationDirPath();
    const QStringList candidates = {
        QDir(appDir).filePath("Unity/SpatialUnity.exe"),
        QDir(appDir).filePath("SpatialUnity.exe"),
        QDir(QDir::cleanPath(QDir(appDir).absoluteFilePath("../../../..")))
            .filePath("SpatialPositionRelation/Build/Windows/SpatialUnity.exe")};
    QString unityPath;
    for (const QString &candidate : candidates) {
        if (QFileInfo::exists(candidate)) { unityPath = QFileInfo(candidate).absoluteFilePath(); break; }
    }
    if (unityPath.isEmpty()) {
        startupFailure("发布目录中缺少 Unity Player");
        return;
    }
    QTcpServer portChooser;
    if (!portChooser.listen(QHostAddress::LocalHost, 0)) {
        startupFailure("无法分配本机 TCP 端口：" + portChooser.errorString());
        return;
    }
    m_unityPort = portChooser.serverPort();
    portChooser.close();
    m_instanceToken = QUuid::createUuid().toString(QUuid::WithoutBraces);
    m_playerLog = m_startupLog.isEmpty() ? QString() : m_startupLog + ".player.log";
    if (!m_playerLog.isEmpty() && QFileInfo::exists(m_playerLog))
        RuntimeData::rotateLog(m_playerLog);
    const quintptr parentHandle = static_cast<quintptr>(m_viewport->winId());
    m_unityProcess->setProgram(unityPath);
    int initialWidth = m_viewport->width();
    int initialHeight = m_viewport->height();
#ifdef Q_OS_WIN
    RECT viewportClient {};
    if (GetClientRect(reinterpret_cast<HWND>(parentHandle), &viewportClient)) {
        initialWidth = viewportClient.right;
        initialHeight = viewportClient.bottom;
    }
#endif
    QStringList arguments{"-parentHWND", QString::number(parentHandle),
                          "-popupwindow", "-screen-width", QString::number(qMax(1, initialWidth)),
                          "-screen-height", QString::number(qMax(1, initialHeight)),
                          "-spatialPort", QString::number(m_unityPort), "-spatialToken", m_instanceToken};
    auto environment = QProcessEnvironment::systemEnvironment();
    if (!m_playerLog.isEmpty()) {
        arguments << "-logFile" << m_playerLog;
        const auto tempProbe = appDataPath(".unity-tmp/.location");
        const auto temp = tempProbe.isEmpty() ? QString() : QFileInfo(tempProbe).absolutePath();
        if (!temp.isEmpty()) {
            environment.insert("TEMP", QDir::toNativeSeparators(temp));
            environment.insert("TMP", QDir::toNativeSeparators(temp));
            arguments << "-crash-report-folder" << temp;
        }
    } else {
        arguments << "-nolog" << "-logFile" << "NUL";
    }
    m_unityProcess->setProcessEnvironment(environment);
    m_unityProcess->setArguments(arguments);
    m_unityProcess->setWorkingDirectory(QFileInfo(unityPath).absolutePath());
    m_unityWindowHandle = 0;
    startupEvent("unity-launch-request", QString("port=%1 parent=%2 playerLog=%3").arg(m_unityPort).arg(parentHandle).arg(m_playerLog));
    m_unityProcess->start();
    m_viewportSyncTimer->start();
    m_connectTimer->start();
    updateStartupStatus();
}

// 定时尝试连接刚启动的 Unity 进程，并等待窗口、身份握手、首帧和首条遥测都出现。
// LocalHost 表示本机；启动 120000 毫秒仍不齐备就报超时，连接检查定时器每 800 毫秒触发。
void MainWindow::tryConnectUnity()
{
    if (m_closing || m_startupFailed) return;
    if (!(m_unityWindowHandle && m_handshake && m_firstFrame && m_firstTelemetry)
        && m_startupClock.elapsed() > 120000) {
        startupFailure(m_connectionLabel->text() + "，等待超时");
        if (m_unityProcess->state() != QProcess::NotRunning) m_unityProcess->kill();
        return;
    }
    if (m_unityProcess->state() == QProcess::Running && m_socket->state() == QAbstractSocket::UnconnectedState)
        m_socket->connectToHost(QHostAddress::LocalHost, m_unityPort);
    updateStartupStatus();
}

void MainWindow::startupEvent(const QString &stage, const QString &details)
{
    if (m_startupLog.isEmpty()) return;
    const QString record = QString("%1 +%2ms %3 %4\n")
        .arg(QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs)).arg(m_startupClock.elapsed()).arg(stage, details);
    RuntimeData::appendLog(m_startupLog, record.toUtf8());
    static const QHash<QString, QString> userEvents = {
        {"qt-created", "空间位置关系计算模块已启动"}, {"unity-launch-request", "开始加载三维场景"},
        {"window-embedded", "Unity 画面已嵌入"}, {"application-ready", "Qt 与 Unity 通信已就绪"},
        {"first-frame", "三维画面已显示"}, {"first-telemetry", "已收到首批敏感器计算结果"},
        {"tcp-disconnected", "Unity 通信连接已断开"}, {"startup-failed", "三维场景启动未完成，请查看运行状态"},
        {"unity-exited", "Unity 已退出"}, {"viewport-resize", "三维画面尺寸已调整"},
        {"dynamics-play", "开始或继续 7DOF 演示"}, {"dynamics-pause", "暂停 7DOF 演示"},
        {"dynamics-reset", "7DOF 演示已请求复位"}, {"dynamics-event", "7DOF 工况"},
        {"dynamics-finished", "7DOF 演示播放完成"}
    };
    const QString description = userEvents.value(stage);
    if (!m_operatorLog.isEmpty() && !description.isEmpty()) {
        const QString localTime = QDateTime::currentDateTime().toString("yyyy-MM-dd HH:mm:ss");
        QString context;
        if (stage == "viewport-resize") context = QString("（%1 像素）").arg(details);
        if (stage == "dynamics-event") context = QString(" · %1").arg(details);
        RuntimeData::appendLog(m_operatorLog, QString("%1  %2%3\n").arg(localTime, description, context).toUtf8());
    }
}

void MainWindow::updateStartupStatus()
{
    if (m_startupFailed || m_closing) return;
    const bool ready = m_unityWindowHandle && m_handshake && m_firstFrame && m_firstTelemetry;
    m_demoButton->setEnabled(ready);
    m_demoButton->setToolTip("播放或暂停 7DOF 工况，复位返回起始状态");
    const QString stage = ready ? "Unity 就绪 · TCP 已连接" :
        !m_handshake ? (m_unityWindowHandle ? "等待 Unity 应用握手" : "Unity 正在加载场景") :
        !m_firstFrame ? "等待 Unity 首帧" : !m_unityWindowHandle ? "等待嵌入 Unity 窗口" : "等待传感器遥测";
    m_connectionLabel->setText(ready ? stage : QString("%1（%2 秒）").arg(stage).arg(m_startupClock.elapsed() / 1000));
    m_connectionLabel->setStyleSheet(ready ? "color:#16845b;font-weight:600;" : "color:#b45309;font-weight:600;");
    m_connectionLabel->setToolTip(m_startupLog);
}

void MainWindow::startupFailure(const QString &reason)
{
    m_startupFailed = true;
    m_connectTimer->stop();
    m_viewportSyncTimer->stop();
    startupEvent("startup-failed", reason);
    m_connectionLabel->setText(reason);
    m_connectionLabel->setStyleSheet("color:#b42318;font-weight:600;");
    m_connectionLabel->setToolTip(m_startupLog);
    m_restartUnity->setVisible(m_unityProcess->state() == QProcess::NotRunning);
}

bool MainWindow::eventFilter(QObject *watched, QEvent *event)
{
    if (watched == m_viewport && event->type() == QEvent::Resize) {
        QTimer::singleShot(0, this, &MainWindow::resizeUnityChild);
    }
    return QMainWindow::eventFilter(watched, event);
}

// 跟随 Qt 视口大小找到并调整 Unity 子窗口，只有 Windows 构建执行窗口句柄操作。
// GetClientRect 取得不含标题栏的客户区像素尺寸；SetWindowPos 调窗口尺寸，窗口外框尺寸需补回边框差。
// 部分 GUI API 用 HWND 表示窗口，成员存整数句柄时经 reinterpret_cast 转回指针式句柄。
void MainWindow::resizeUnityChild()
{
#ifdef Q_OS_WIN
    if (!m_viewport || !m_unityProcess || m_unityProcess->state() == QProcess::NotRunning) return;
    HWND child = reinterpret_cast<HWND>(m_unityWindowHandle);
    if (!child || !IsWindow(child)) {
        m_unityWindowHandle = 0;
        struct WindowSearch { DWORD processId; HWND match; } search {
            static_cast<DWORD>(m_unityProcess->processId()), nullptr
        };
        EnumChildWindows(reinterpret_cast<HWND>(m_viewport->winId()),
            [](HWND candidate, LPARAM data) -> BOOL {
                auto *query = reinterpret_cast<WindowSearch *>(data);
                wchar_t className[128] = {};
                DWORD processId = 0;
                GetClassNameW(candidate, className, 128);
                GetWindowThreadProcessId(candidate, &processId);
                if (processId == query->processId && wcscmp(className, L"UnityWndClass") == 0) {
                    query->match = candidate;
                    return FALSE;
                }
                return TRUE;
            }, reinterpret_cast<LPARAM>(&search));
        if (!search.match) return;
        child = search.match;
        m_unityWindowHandle = reinterpret_cast<quintptr>(child);
        startupEvent("window-embedded", QString::number(m_unityWindowHandle));
        updateStartupStatus();
    }
    if (!m_firstFrame && (GetWindowLongPtrW(child, GWLP_USERDATA) & 1) == 0) return;
    RECT viewportClient {};
    if (!GetClientRect(reinterpret_cast<HWND>(m_viewport->winId()), &viewportClient)) return;
    const int targetWidth = qMax(1, viewportClient.right - viewportClient.left);
    const int targetHeight = qMax(1, viewportClient.bottom - viewportClient.top);
    RECT bounds {};
    GetWindowRect(child, &bounds);
    RECT childClient {};
    if (!GetClientRect(child, &childClient)) return;
    if (childClient.right != targetWidth || childClient.bottom != targetHeight) {
        const int frameWidth = bounds.right - bounds.left - childClient.right;
        const int frameHeight = bounds.bottom - bounds.top - childClient.bottom;
        if (SetWindowPos(child, nullptr, 0, 0, targetWidth + frameWidth, targetHeight + frameHeight,
                         SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS))
            startupEvent("viewport-resize", QString("%1×%2").arg(targetWidth).arg(targetHeight));
    }
#endif
}

// 用户关窗而 Unity 仍运行时先暂缓 Qt 退出，转入有序关停，避免留下子进程。
// 这是 Qt 关闭事件回调；event->ignore() 暂不接受本次关闭，子进程结束后 finishUnityCleanup 再关闭窗口。
void MainWindow::closeEvent(QCloseEvent *event)
{
    if (m_unityProcess->state() == QProcess::NotRunning) {
        if (!m_closing) startupEvent("qt-close-no-child");
        QMainWindow::closeEvent(event);
        return;
    }
    event->ignore();
    if (m_closing) return;
    m_closing = true;
    startupEvent("qt-close-request");
    m_connectionLabel->setText("正在关闭 Unity…");
    stopUnity();
}

// 先停定时器，再通过 SPR/1 shutdown 请求结束正确的 Unity 实例，并在超时后强制清理。
// 握手完成时发协议消息和 instanceToken；未完成则 terminate 子进程。
// 1800 毫秒后仅在同一令牌的进程还活着时 kill，防止过时定时器误杀重启后的新实例。
void MainWindow::stopUnity()
{
    m_connectTimer->stop();
    m_viewportSyncTimer->stop();
    m_sendTimer->stop();
    m_frameTimer->stop();
    if (m_handshake) {
        const QJsonObject shutdown{{"protocol", "SPR/1"}, {"type", "shutdown"}, {"instanceToken", m_instanceToken}};
        m_socket->write(QJsonDocument(shutdown).toJson(QJsonDocument::Compact) + '\n');
    } else {
        m_unityProcess->terminate();
    }
    const QString token = m_instanceToken;
    QTimer::singleShot(1800, this, [this, token] {
        if (token == m_instanceToken && m_unityProcess->state() != QProcess::NotRunning) {
            startupEvent("unity-cleanup-force", QString::number(m_unityProcess->processId()));
            m_unityProcess->kill();
        }
    });
}

void MainWindow::finishUnityCleanup()
{
    startupEvent("qt-cleanup-complete");
    QTimer::singleShot(0, this, [this] { close(); });
}

QString MainWindow::appDataPath(const QString &relative) const
{
    return RuntimeData::path(m_dataRoot, relative);
}

void MainWindow::appendProcessLog(const QByteArray &data)
{
    RuntimeData::appendLog(m_processLog, data);
}

// 为本次运行创建带文件锁的一组日志路径，防止两次程序实例同时写同一会话。
// QLockFile 是 Qt 的文件锁；日志文件名含 UTC 时间和进程 ID，再交给 RuntimeData 清理旧日志。
bool MainWindow::prepareDataSession(const QString &root)
{
    if (root.isEmpty()) return false;
    const QString base = QString("logs/startup_%1_%2").arg(RuntimeData::utcFileTimestamp()).arg(QCoreApplication::applicationPid());
    const QString lockPath = RuntimeData::path(root, base + ".lock");
    if (lockPath.isEmpty()) return false;
    auto lock = std::make_unique<QLockFile>(lockPath);
    lock->setStaleLockTime(0);
    if (!lock->tryLock()) return false;
    const QString startupLog = RuntimeData::path(root, base + ".log");
    if (startupLog.isEmpty()) return false;
    m_retiredSessionLock = std::move(m_sessionLock);
    m_sessionLock = std::move(lock);
    m_dataRoot = root;
    m_startupLog = startupLog;
    m_operatorLog = RuntimeData::path(root, base + "-运行记录.txt");
    m_tcpInLog = startupLog + ".tcp.jsonl";
    m_tcpOutLog = startupLog + ".tx.jsonl";
    m_processLog = startupLog + ".stdout";
    RuntimeData::pruneLogs(root);
    return true;
}

void MainWindow::updateDataStatus()
{
    m_dataStatus->setText(m_dataRoot.isEmpty() ? "只读模式 · 未设置可写数据目录" : "数据保存目录已设置");
    m_dataStatus->setToolTip(QDir::toNativeSeparators(m_dataRoot));
}

bool MainWindow::requireWritableData()
{
    if (!m_dataRoot.isEmpty()) return true;
    statusBar()->showMessage("只读模式。可在「运行状态」中设置非 C 盘数据目录。", 6000);
    return false;
}

void MainWindow::showRunStatus()
{
    QMessageBox dialog(this);
    dialog.setWindowTitle("运行状态");
    dialog.setTextFormat(Qt::PlainText);
    dialog.setText(m_connectionLabel->text());
    dialog.setInformativeText(QString("已走完的步骤\n%1\n%2\n%3\n%4\n\n数据保存目录：%5")
        .arg(m_unityWindowHandle ? "已找到 Unity 画面" : "正在等待 Unity 画面")
        .arg(m_handshake ? "已连接 Unity" : "正在等待 Unity 通信")
        .arg(m_firstFrame ? "已显示首帧" : "正在等待首帧")
        .arg(m_firstTelemetry ? "已收到传感器结果" : "正在等待传感器结果")
        .arg(m_dataRoot.isEmpty() ? "只读模式" : QDir::toNativeSeparators(m_dataRoot)));
    auto *change = dialog.addButton("更改", QMessageBox::ActionRole);
    auto *open = dialog.addButton("打开数据文件夹", QMessageBox::ActionRole);
    auto *operatorLog = dialog.addButton("查看中文运行记录", QMessageBox::ActionRole);
    auto *diagnostic = dialog.addButton("诊断文件…", QMessageBox::ActionRole);
    operatorLog->setEnabled(!m_operatorLog.isEmpty() && QFile::exists(m_operatorLog));
    change->setEnabled(!m_relaunchUnity && !m_closing);
    open->setEnabled(!m_dataRoot.isEmpty());
    diagnostic->setEnabled(!m_startupLog.isEmpty());
    dialog.addButton("关闭", QMessageBox::RejectRole);
    dialog.exec();
    if (dialog.clickedButton() == change) changeDataRoot();
    else if (dialog.clickedButton() == open) QDesktopServices::openUrl(QUrl::fromLocalFile(m_dataRoot));
    else if (dialog.clickedButton() == operatorLog) {
        QFile records(m_operatorLog);
        if (!records.open(QIODevice::ReadOnly)) {
            QMessageBox::warning(this, "无法读取运行记录", "请检查数据目录是否可读。");
            return;
        }
        QDialog viewer(this);
        viewer.setWindowTitle("中文运行记录");
        viewer.resize(610, 360);
        auto *layout = new QVBoxLayout(&viewer);
        auto *text = new QPlainTextEdit(&viewer);
        text->setReadOnly(true);
        text->setPlainText(QString::fromUtf8(records.readAll()));
        layout->addWidget(text);
        auto *buttons = new QDialogButtonBox(&viewer);
        buttons->addButton("关闭", QDialogButtonBox::RejectRole);
        connect(buttons, &QDialogButtonBox::rejected, &viewer, &QDialog::reject);
        layout->addWidget(buttons);
        viewer.exec();
    } else if (dialog.clickedButton() == diagnostic) {
        QMessageBox details(QMessageBox::Information, "诊断文件",
            "供开发排查使用。此目录包含原始 TCP JSON、Unity 引擎日志及模型部件标识。",
            QMessageBox::NoButton, this);
        auto *openDiagnostics = details.addButton("打开诊断目录", QMessageBox::ActionRole);
        details.addButton("关闭", QMessageBox::RejectRole);
        details.exec();
        if (details.clickedButton() == openDiagnostics)
            QDesktopServices::openUrl(QUrl::fromLocalFile(QFileInfo(m_startupLog).absolutePath()));
    }
}

void MainWindow::changeDataRoot()
{
    if (m_relaunchUnity || m_closing) return;
    const QString selected = QFileDialog::getExistingDirectory(this, "选择非 C 盘的数据目录",
        m_dataRoot.isEmpty() ? QCoreApplication::applicationDirPath() : m_dataRoot);
    if (selected.isEmpty()) return;
    QString error;
    const QString root = RuntimeData::validateRoot(selected, error);
    if (root.isEmpty()) {
        QMessageBox::warning(this, "无法更改数据目录", error);
        return;
    }
    if (root.compare(m_dataRoot, Qt::CaseInsensitive) == 0) return;
    const QJsonObject state = currentState();
    const QString configPath = RuntimeData::path(root, "config/user_config.json");
    if (QFileInfo::exists(configPath) && QMessageBox::question(this, "使用现有数据目录",
            "继续后会将当前参数保存到该目录，替换已有参数文件。\n" + configPath) != QMessageBox::Yes) return;
    if (!RuntimeData::saveJson(configPath, state) || !prepareDataSession(root)) {
        QMessageBox::warning(this, "无法更改数据目录", "未能保存参数或打开日志会话。\n" + root);
        return;
    }
    m_config = state;
    const bool persist = RuntimeData::storeRoot(root, error);
    if (m_frameRecording) toggleFrameRecording();
    m_pendingFrameSequence = 0;
    startupEvent("data-root-changed", root);
    updateDataStatus();
    statusBar()->showMessage("数据目录已更改，Unity 正在重新连接：" + root, 8000);
    if (m_unityProcess->state() != QProcess::NotRunning) {
        m_relaunchUnity = true;
        m_connectionLabel->setText("正在重新打开 Unity 日志…");
        stopUnity();
    } else {
        m_retiredSessionLock.reset();
        startUnity();
    }
    if (!persist) QMessageBox::information(this, "数据目录仅本次生效", error);
}

void MainWindow::maintainLogs()
{
    if (m_dataRoot.isEmpty() || m_closing || m_relaunchUnity) return;
    if (m_unityProcess->state() == QProcess::Running && !m_playerLog.isEmpty()
        && QFileInfo(m_playerLog).size() >= 124ll * 1024 * 1024) {
        // The native Player owns the file handle. Close it before rotating.
        startupEvent("unity-log-rotate", m_playerLog);
        m_rotatePlayer = m_relaunchUnity = true;
        m_connectionLabel->setText("正在滚动 Unity 日志并重新连接…");
        stopUnity();
    }
    if (++m_logGuardTicks >= 30) {
        m_logGuardTicks = 0;
        RuntimeData::pruneLogs(m_dataRoot);
    }
}
