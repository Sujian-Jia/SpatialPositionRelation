#ifndef MAINWINDOW_H
#define MAINWINDOW_H

#include <QMainWindow>
#include <QJsonArray>
#include <QJsonObject>
#include <QByteArray>
#include <QHash>
#include <QVector>
#include <QElapsedTimer>
#include <memory>

class QLockFile;

class QCheckBox;
class QCloseEvent;
class QComboBox;
class QDateTimeEdit;
class QDoubleSpinBox;
class QEvent;
class QLabel;
class QTableWidget;
class QProcess;
class QPushButton;
class QSlider;
class QSplitter;
class QTcpSocket;
class QTimer;
class QWidget;

// 这是 Qt 主控窗口的类声明；真正的界面创建和消息处理实现在分开的 .cpp 文件。
// public 是外部可调用部分，protected 接收窗口事件，private 放内部控件和运行状态。
class MainWindow : public QMainWindow
{
    Q_OBJECT

public:
    explicit MainWindow(const QString &dataRoot, QWidget *parent = nullptr);
    ~MainWindow() override;

protected:
    bool eventFilter(QObject *watched, QEvent *event) override;
    void closeEvent(QCloseEvent *event) override;

private:
    void buildUi();
    void loadConfig();
    void connectControls();
    // 启动 Unity 子进程并准备 Qt 与 Unity 的本机连接。
    void startUnity();
    void stopUnity();
    void showRunStatus();
    void changeDataRoot();
    bool prepareDataSession(const QString &root);
    void updateDataStatus();
    bool requireWritableData();
    void maintainLogs();
    void tryConnectUnity();
    void startupEvent(const QString &stage, const QString &details = {});
    void updateStartupStatus();
    void startupFailure(const QString &reason);
    void finishUnityCleanup();
    void sendState();
    // 读取界面当前值并拼成一份状态快照。
    QJsonObject currentState() const;
    // 处理 Unity 回来的一整行 JSON。
    void receiveMessage(const QByteArray &line);
    void showTelemetry(const QJsonObject &message);
    void updateSolarControls();
    void editSelectedSensor();
    void applySensorEditor();
    void saveConfig();
    void saveSelectedTelemetry();
    void captureFrame();
    void recordFrame();
    void requestSensorFrame(const QString &id, bool recording);
    void saveSensorFrame(const QJsonObject &message);
    void toggleFrameRecording();
    void resizeUnityChild();
    QString appDataPath(const QString &relative) const;
    QString safeFileName(const QString &value) const;
    QDoubleSpinBox *makeSpin(double minimum, double maximum, double step, int decimals);
    void appendProcessLog(const QByteArray &data);

    QWidget *m_controlPanel = nullptr;
    QWidget *m_viewport = nullptr;
    QSplitter *m_mainSplitter = nullptr;
    QLabel *m_connectionLabel = nullptr;
    QPushButton *m_restartUnity = nullptr;
    QElapsedTimer m_startupClock;
    QString m_dataRoot;
    QString m_startupLog;
    QString m_operatorLog;
    QString m_tcpOutLog;
    QString m_tcpInLog;
    QString m_processLog;
    std::unique_ptr<QLockFile> m_sessionLock;
    std::unique_ptr<QLockFile> m_retiredSessionLock;
    QLabel *m_dataStatus = nullptr;
    bool m_relaunchUnity = false;
    bool m_rotatePlayer = false;
    int m_logGuardTicks = 0;
    QString m_playerLog;
    QString m_instanceToken;
    quint16 m_unityPort = 0;
    quintptr m_job = 0;
    bool m_handshake = false;
    bool m_firstFrame = false;
    bool m_firstTelemetry = false;
    bool m_closing = false;
    bool m_startupFailed = false;
    QLabel *m_distanceLabel = nullptr;
    QLabel *m_centerDistanceLabel = nullptr;
    QLabel *m_axialGapLabel = nullptr;
    QLabel *m_toolDistanceLabel = nullptr;
    QLabel *m_stationAttitude = nullptr;
    QLabel *m_visitorAttitude = nullptr;
    QDoubleSpinBox *m_dynamicsSpeed = nullptr;
    QLabel *m_sensorSummary = nullptr;
    QTableWidget *m_telemetryTable = nullptr;
    QLabel *m_demoStatus = nullptr;
    QLabel *m_wingStatus = nullptr;
    QLabel *m_wingControlHint = nullptr;
    bool m_targetHasWings = false;
    bool m_chaserHasWings = false;
    QPushButton *m_demoButton = nullptr;
    QCheckBox *m_showRays = nullptr;
    QCheckBox *m_manualSolarDiagnostic = nullptr;
    QComboBox *m_manualSolarRole = nullptr;
    QComboBox *m_raySensor = nullptr;
    QLabel *m_sensorPositionReference = nullptr;
    QJsonObject m_defaultConfig;
    bool m_demoActive = false;
    bool m_demoFinished = false;
    int m_demoRunId = 0;
    int m_dynamicsEventsShown = 0;
    QComboBox *m_sensorCombo = nullptr;
    QSlider *m_solarSlider = nullptr;
    QVector<QLabel *> m_jointValueLabels;
    QVector<QDoubleSpinBox *> m_orbitSpins;
    QDateTimeEdit *m_epochUtcEdit = nullptr;
    QDoubleSpinBox *m_fovHorizontal = nullptr;
    QDoubleSpinBox *m_fovVertical = nullptr;
    QVector<QDoubleSpinBox *> m_sensorPositionSpins;
    QVector<QDoubleSpinBox *> m_sensorRotationSpins;
    QCheckBox *m_warningEnabled = nullptr;
    QPushButton *m_recordButton = nullptr;

    QTcpSocket *m_socket = nullptr;
    QProcess *m_unityProcess = nullptr;
    QTimer *m_connectTimer = nullptr;
    QTimer *m_sendTimer = nullptr;
    QTimer *m_frameTimer = nullptr;
    QTimer *m_viewportSyncTimer = nullptr;
    QJsonObject m_config;
    QJsonArray m_sensorConfigs;
    QHash<QString, QJsonObject> m_lastTelemetry;
    QByteArray m_receiveBuffer;
    QString m_recordingSensorId;
    QString m_recordingDirectory;
    QString m_pendingFrameSensor;
    quint64 m_sequence = 0;
    quint64 m_frameIndex = 0;
    quint64 m_pendingFrameSequence = 0;
    bool m_pendingFrameRecording = false;
    bool m_frameRecording = false;
    bool m_loadingSensorEditor = false;
    quintptr m_unityWindowHandle = 0;
};
#endif // MAINWINDOW_H
