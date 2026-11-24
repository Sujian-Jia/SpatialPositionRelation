#include "telemetrydisplay.h"

#include <QJsonArray>
#include <QMap>
#include <QRegularExpression>

namespace {
QString blockersText(const QJsonArray &blockers, const QString &chaserCraftId)
{
    QMap<QString, int> counts;
    static const QRegularExpression cadIdentifier("[A-Fa-f0-9]{20,}|MANIFOLD_SOLID_BREP", QRegularExpression::CaseInsensitiveOption);
    for (const auto &value : blockers) {
        const QJsonObject hit = value.toObject();
        const QString owner = hit.value("ownerCraftId").toString();
        QString part = hit.value("partName").toString(hit.value("displayName").toString());
        if (part.isEmpty() || cadIdentifier.match(part).hasMatch()) {
            const QString category = hit.value("category").toString();
            part = category == "SelfArm" ? "机械臂" : category == "SolarArray" ? "太阳翼" : "舱体结构件";
        }
        const QString role = owner.isEmpty() ? "归属未标记的" : owner == chaserCraftId ? "己方航天器的" : "对方航天器的";
        ++counts[role + part];
    }
    QStringList text;
    for (auto it = counts.cbegin(); it != counts.cend(); ++it)
        text.append(QString("%1（%2 件）").arg(it.key()).arg(it.value()));
    return text.isEmpty() ? QString("—") : text.join("、");
}

QString numeric(const QJsonValue &value, double scale = 1, int digits = 1)
{
    if (!value.isDouble()) return "无数据";
    const double scaled = value.toDouble() * scale;
    if (scaled > 0 && digits == 1 && scaled < 0.05) return "<0.1";
    return QString::number(scaled, 'f', digits);
}
}

QStringList TelemetryDisplay::headers()
{
    return {"敏感器", "采样点入场", "当前遮挡", "未来预警 (s)", "可见点/总点数", "面积入场/可见 (%)",
        "太阳入射 (°)", "直射受光 (%)", "遮挡来源", "建议"};
}

QStringList TelemetryDisplay::headerHelp()
{
    return {"当前追踪航天器的敏感器", "仅判断目标采样点的几何入场。全部、部分、出场分别对应全部、部分、零个点落入视锥。",
        "对入场点发出视线并判断是否被结构遮挡。", "演示轨迹上的预计遮挡时间段，单位为仿真秒。",
        "入场且未被遮挡的点数 ÷ 全部目标点数。CRDS 使用对接标志采样，电视相机使用目标表面或指定特征采样。",
        "目标估算投影面积比例。第一个值仅判断视场，第二个值扣除遮挡。",
        "镜头入口朝外法线与指向太阳的方向之间的夹角。", "无遮挡且入口正对太阳为 100%。数值由入射余弦、太阳圆盘可见比例和地影共同决定。",
        "按实际命中碰撞件及其航天器实例分组。己方为本行敏感器所属航天器。鼠标悬停可看完整列表。", "当前几何受光或遮挡的调整建议。"};
}

QString TelemetryDisplay::illuminationHelp(const QJsonObject &sensor)
{
    const auto reason = sensor.value("illuminationReason").toString();
    if (reason == "earthShadow") return "航天器处于地影";
    if (reason == "behindEntrance") return "太阳在镜头入口背面";
    if (reason == "structureShadow") return "太阳圆盘采样全部被结构遮挡";
    if (reason == "direct") return QString("入射余弦 %1，太阳圆盘可见 %2%")
        .arg(numeric(sensor.value("incidenceCosine"), 1, 4), numeric(sensor.value("sunVisibleFraction"), 100));
    return {};
}

QStringList TelemetryDisplay::row(const QJsonObject &sensor, const QString &name, const QString &chaserCraftId, bool predicting)
{
    const auto status = sensor.value("status").toString();
    const bool future = predicting && sensor.value("futureOcclusion").toBool();
    const QJsonValue totalValue = sensor.value("totalPointCount");
    const int total = totalValue.toInt();
    const int inFov = sensor.value("inFovCount").toInt();
    const int visible = sensor.value("visibleCount").toInt();
    QString fieldOfView = "无数据";
    QString points = "无数据";
    if (totalValue.isDouble()) {
        fieldOfView = total <= 0 ? "未配置" : inFov == 0 ? "出场" : inFov == total ? "全部入场" : "部分入场";
        points = total <= 0 ? "未配置" : QString("%1/%2（%3%）").arg(visible).arg(total)
            .arg(QString::number(double(visible) / total * 100, 'f', 1));
    }
    QString recommendation = sensor.value("recommendation").toString();
    if (!sensor.value("targetResolved").toBool(true)) recommendation.prepend("按当前目标计算 · ");
    if (sensor.value("warning").toBool()) recommendation.prepend("光照告警 · ");
    const bool lightAvailable = sensor.value("illuminationAvailable").toBool(true);
    return {name, fieldOfView, status == "FullyBlocked" ? "完全遮挡" : status == "PartiallyBlocked" ? "部分遮挡" : "—",
        future ? QString("预警 %1–%2").arg(numeric(sensor.value("futureOcclusionStartSec")), numeric(sensor.value("futureOcclusionEndSec"))) : "—",
        points, numeric(sensor.value("targetAreaInFovRatio"), 100, 0) + " / " + numeric(sensor.value("visibleTargetAreaRatio"), 100, 0),
        lightAvailable ? numeric(sensor.value("sunIncidenceDeg")) : "无数据",
        lightAvailable ? numeric(sensor.value("normalizedIllumination"), 100) : "无数据",
        blockersText(sensor.value("blockers").toArray(), chaserCraftId), recommendation};
}
