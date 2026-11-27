#include "runtimedata.h"
#include <QDateTime>

#include <QCoreApplication>
#include <QDir>
#include <QJsonDocument>
#include <QJsonObject>
#include <QFile>
#include <QFileInfo>
#include <QLockFile>
#include <QRegularExpression>
#include <QSaveFile>
#include <QTemporaryFile>

#ifdef Q_OS_WIN
#include <windows.h>
#endif

namespace {
// Resolve the nearest existing directory to catch links and drive aliases before creating a path.
bool onAllowedVolume(const QString &path)
{
#ifdef Q_OS_WIN
    static const QRegularExpression drive(R"(^[A-Za-z]:/)");
    if (!drive.match(path).hasMatch() || path.startsWith("C:/", Qt::CaseInsensitive)) return false;
#endif
    QFileInfo ancestor(path);
    while (!ancestor.exists()) {
        if (ancestor.isSymLink() || ancestor.isJunction()) return false;
        const QString parent = ancestor.absolutePath();
        if (parent == ancestor.absoluteFilePath()) return false;
        ancestor.setFile(parent);
    }
    const QString canonical = QDir::fromNativeSeparators(ancestor.canonicalFilePath());
    if (canonical.isEmpty()) return false;
#ifdef Q_OS_WIN
    if (!drive.match(canonical).hasMatch() || canonical.startsWith("C:/", Qt::CaseInsensitive)) return false;
    const auto native = QDir::toNativeSeparators(canonical).toStdWString();
    const HANDLE handle = CreateFileW(native.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                      nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return false;
    wchar_t target[32768]{};
    const DWORD size = GetFinalPathNameByHandleW(handle, target, DWORD(std::size(target)), VOLUME_NAME_GUID);
    CloseHandle(handle);
    wchar_t systemVolume[64]{};
    if (!size || size >= std::size(target) || !GetVolumeNameForVolumeMountPointW(L"C:\\", systemVolume, 64)) return false;
    const QString resolved = QString::fromWCharArray(target, int(size));
    if (!resolved.startsWith(QStringLiteral(R"(\\?\Volume{)"), Qt::CaseInsensitive)
        || resolved.startsWith(QString::fromWCharArray(systemVolume), Qt::CaseInsensitive)) return false;
#endif
    return true;
}

bool probe(const QString &directory)
{
    if (!onAllowedVolume(directory) || !QDir().mkpath(directory)) return false;
    QTemporaryFile file(QDir(directory).filePath(".write-check-XXXXXX"));
    return file.open() && file.write("ok", 2) == 2 && file.flush();
}
}

bool RuntimeData::isPermittedPath(const QString &path)
{
    return onAllowedVolume(QDir::fromNativeSeparators(path));
}

QString RuntimeData::defaultRoot()
{
    return QDir(QCoreApplication::applicationDirPath()).filePath("data");
}

QString RuntimeData::pointerPath()
{
    return QDir(QCoreApplication::applicationDirPath()).filePath("config/data_root.txt");
}

QString RuntimeData::requestedRoot(const QString &argument)
{
    if (!argument.isEmpty()) return argument;
    QFile pointer(pointerPath());
    if (pointer.open(QIODevice::ReadOnly)) {
        const QString saved = QString::fromUtf8(pointer.read(32768)).trimmed();
        if (!saved.isEmpty()) return QDir(QCoreApplication::applicationDirPath()).absoluteFilePath(saved);
    }
    return defaultRoot();
}

// 检查用户指定的数据目录是否符合本机存储策略，并验证能否写入。
// error 是引用输出参数，函数可同时返回处理后的路径和说明文本；调用处依据空路径决定是否进入只读模式。
QString RuntimeData::validateRoot(const QString &input, QString &error)
{
    const QString path = QDir::cleanPath(QDir::fromNativeSeparators(QFileInfo(input).absoluteFilePath()));
    if (input.trimmed().isEmpty() || !onAllowedVolume(path)) {
        error = "请选择位于非 C 盘的本地目录。";
        return {};
    }
    for (const QString &relative : { QString(), QString("config"), QString("logs"), QString("output") }) {
        if (!probe(relative.isEmpty() ? path : QDir(path).filePath(relative))) {
            error = "目录不可写或指向 C 盘：\n" + QDir(path).filePath(relative);
            return {};
        }
    }
    return QDir::fromNativeSeparators(QFileInfo(path).canonicalFilePath());
}

bool RuntimeData::storeRoot(const QString &root, QString &error)
{
    const QString filepath = pointerPath();
    if (!onAllowedVolume(filepath) || !QDir().mkpath(QFileInfo(filepath).absolutePath())) {
        error = "无法在程序目录保存目录设置。下次可通过 --data-root 指定。";
        return false;
    }
    const QString relative = QDir(QCoreApplication::applicationDirPath()).relativeFilePath(root);
    const QByteArray content = ((relative == "data") ? relative : root).toUtf8() + '\n';
    QSaveFile file(filepath);
    if (!file.open(QIODevice::WriteOnly) || file.write(content) != content.size() || !file.commit()) {
        error = "无法保存目录设置：\n" + filepath;
        return false;
    }
    return true;
}

// 在获准的数据根目录下拼一个文件路径，拒绝绝对路径和跳出根目录的相对路径。
// canonical path 是解析符号链接等别名后的路径；实际使用前还检查目录所在盘并创建父目录。
QString RuntimeData::path(const QString &root, const QString &relative)
{
    if (root.isEmpty() || QDir::isAbsolutePath(relative)) return {};
    const QString filepath = QDir::cleanPath(QDir(root).filePath(relative));
    if (!filepath.startsWith(root.endsWith('/') ? root : root + "/", Qt::CaseInsensitive)) return {};
    // Root and all existing nested directories must resolve to an approved volume.
    if (!onAllowedVolume(filepath) || !QDir().mkpath(QFileInfo(filepath).absolutePath())) return {};
    return filepath;
}

// 把一个 JSON 对象保存成带缩进的数据文件。
// QSaveFile 在临时文件写完后提交，函数返回 bool 告诉调用方是否保存成功。
bool RuntimeData::saveJson(const QString &path, const QJsonObject &value)
{
    if (path.isEmpty()) return false;
    const QByteArray data = QJsonDocument(value).toJson(QJsonDocument::Indented);
    QSaveFile file(path);
    return file.open(QIODevice::WriteOnly) && file.write(data) == data.size() && file.commit();
}

bool RuntimeData::rotateLog(const QString &path)
{
    if (path.isEmpty() || !QFileInfo::exists(path)) return false;
    const QString previous = path + ".previous";
    if (QFileInfo::exists(previous) && !QFile::remove(previous)) return false;
    return QFile::rename(path, previous);
}

// 把一段日志追加到文件，超过容量上限时先轮换旧日志。
// limit 和 data.size() 的单位都是字节；单条数据本身大于上限时直接跳过。
void RuntimeData::appendLog(const QString &path, const QByteArray &data, qint64 limit)
{
    if (path.isEmpty() || data.isEmpty() || data.size() > limit) return;
    if (QFileInfo(path).size() > limit - data.size() && !rotateLog(path)) return;
    QFile file(path);
    if (file.open(QIODevice::WriteOnly | QIODevice::Append)) file.write(data);
}

// 在会话锁允许时清理超出总容量或单文件容量的旧日志。
// 会话文件锁避免删掉正在写入的文件；这里的 512 MiB 和 128 MiB 是保留上限，不是空间计算参数。
void RuntimeData::pruneLogs(const QString &root)
{
    if (root.isEmpty()) return;
    const QDir logs(QDir(root).filePath("logs"));
    if (!onAllowedVolume(logs.path())) return;
    QLockFile maintenance(logs.filePath(".maintenance.lock"));
    if (!maintenance.tryLock()) return;
    static const QRegularExpression sessionPattern(R"(^(startup_[0-9]+_[0-9]+_[0-9]+_[0-9]+)\.)");
    struct Entry { QFileInfo info; QString lockPath; };
    QList<Entry> entries;
    qint64 total = 0;
    for (const QFileInfo &info : logs.entryInfoList(QDir::Files | QDir::NoSymLinks, QDir::Time | QDir::Reversed)) {
        const auto match = sessionPattern.match(info.fileName());
        if (!match.hasMatch() || info.suffix() == "lock") continue;
        entries.append({info, logs.filePath(match.captured(1) + ".lock")});
        total += info.size();
    }
    constexpr qint64 totalLimit = 512ll * 1024 * 1024;
    constexpr qint64 fileLimit = 128ll * 1024 * 1024;
    for (const Entry &entry : entries) {
        if (total <= totalLimit && entry.info.size() <= fileLimit) continue;
        QLockFile session(entry.lockPath);
        session.setStaleLockTime(0);
        if (session.tryLock() && QFile::remove(entry.info.absoluteFilePath())) total -= entry.info.size();
    }
}

QString RuntimeData::utcFileTimestamp()
{
    return QDateTime::currentDateTimeUtc().toString("yyyyMMdd_HHmmss_zzz");
}
