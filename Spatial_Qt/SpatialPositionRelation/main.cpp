#include "mainwindow.h"

#include <QApplication>
#include <QCommandLineParser>
#include <QFileDialog>
#include <QMessageBox>

#include "runtimedata.h"

// 这是 Qt 桌面程序的进程入口：先选数据目录，再创建主窗口并进入事件循环。
// QApplication 管理窗口和鼠标等事件；app.exec() 持续分发事件，关闭后 main 才返回。
// 路径和数据检查通过 RuntimeData 集中处理，窗口打开前确定本次运行的数据根目录。
int main(int argc, char *argv[])
{
    QApplication app(argc, argv);
    QCoreApplication::setOrganizationName("SpatialPositionRelation");
    QCoreApplication::setApplicationName("SpatialPositionRelation");
    QCommandLineParser parser;
    parser.addHelpOption();
    QCommandLineOption rootOption("data-root", "使用指定非 C 盘数据目录", "directory");
    parser.addOption(rootOption);
    parser.process(app);

    QString input = RuntimeData::requestedRoot(parser.value(rootOption));
    QString error;
    QString root = RuntimeData::validateRoot(input, error);
    bool picked = false;
    if (!RuntimeData::isPermittedPath(QCoreApplication::applicationDirPath())) {
        const QString message = "程序位于 C 盘或非本地磁盘。请选择非 C 盘的数据目录。";
        if (!root.isEmpty()) {
            const auto choice = QMessageBox::question(nullptr, "数据保存目录", message +
                "\n\n使用已指定的目录：\n" + root + "\n\n取消后将以只读模式运行。",
                QMessageBox::Ok | QMessageBox::Cancel, QMessageBox::Ok);
            if (choice != QMessageBox::Ok) {
                MainWindow window(QString{});
                window.show();
                return app.exec();
            }
        } else {
            error.prepend(message + "\n");
        }
    }
    while (root.isEmpty()) {
        const auto choice = QMessageBox::warning(nullptr, "数据保存目录", error + "\n\n取消后将以只读模式运行。",
                                                 QMessageBox::Open | QMessageBox::Cancel, QMessageBox::Open);
        if (choice != QMessageBox::Open) break;
        input = QFileDialog::getExistingDirectory(nullptr, "选择非 C 盘的数据目录", QCoreApplication::applicationDirPath());
        if (input.isEmpty()) break;
        root = RuntimeData::validateRoot(input, error);
        picked = !root.isEmpty();
    }
    if (picked && !RuntimeData::storeRoot(root, error))
        QMessageBox::information(nullptr, "数据目录仅本次生效", error);
    MainWindow window(root);
    window.show();
    return app.exec();
}
