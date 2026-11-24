#pragma once

#include <QString>

class QJsonObject;

namespace RuntimeData {
QString utcFileTimestamp();
bool isPermittedPath(const QString &path);
QString defaultRoot();
QString pointerPath();
QString requestedRoot(const QString &argument);
QString validateRoot(const QString &path, QString &error);
bool storeRoot(const QString &root, QString &error);
QString path(const QString &root, const QString &relative);
bool saveJson(const QString &path, const QJsonObject &value);
bool rotateLog(const QString &path);
void appendLog(const QString &path, const QByteArray &data, qint64 limit = 128 * 1024 * 1024);
void pruneLogs(const QString &root);
}
