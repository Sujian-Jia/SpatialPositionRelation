#pragma once
#include <QJsonObject>
#include <QStringList>

namespace TelemetryDisplay {
QStringList headers();
QStringList headerHelp();
QStringList row(const QJsonObject &sensor, const QString &name, const QString &chaserCraftId, bool predicting);
QString illuminationHelp(const QJsonObject &sensor);
}
