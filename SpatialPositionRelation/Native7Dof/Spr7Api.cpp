#include "Spr7Api.h"
#include "DemoSession.hpp"
#include <algorithm>

namespace {
using namespace docking;
using namespace docking::demo;
constexpr int kSampleSteps = 20;
constexpr int kDurationSteps = 80000;
constexpr double kStep = 0.001;
static_assert(sizeof(Spr7Frame) == 168 * sizeof(double));

SimulationSettings ArmSettings() {
    auto settings = DefaultSettings();
    settings.scenario = DemoScenario::ArmCapture;
    settings.lockModel = StationLockModel::RigidComposite;
    settings.durationSeconds = kDurationSteps * kStep;
    settings.integrationStep = kStep;
    settings.outputStep = kSampleSteps * kStep;
    return settings;
}

template<typename T, std::size_t N> void Copy(double (&out)[N], const T& vector) {
    std::copy_n(vector.data(), N, out);
}

void Pose(double (&out)[7], const Vec3& point, const Vec4& quaternion) {
    std::copy_n(point.data(), 3, out);
    std::copy_n(quaternion.data(), 4, out + 3);
}
}

int32_t Spr7_Abi() { return 1; }

void* Spr7_Create() {
    try { return new Session(ArmSettings()); }
    catch (...) { return nullptr; }
}

void Spr7_Destroy(void* session) { delete static_cast<Session*>(session); }

int32_t Spr7_Advance(void* handle, int32_t maxSteps) {
    if (!handle || maxSteps < 1) return -1;
    try {
        auto& session = *static_cast<Session*>(handle);
        const int step = static_cast<int>(session.step);
        const int count = std::min({maxSteps, kSampleSteps - step % kSampleSteps, kDurationSteps - step});
        // Preserve the native CLI event clock at CSV boundaries. The RK4 step
        // stays exactly 1 ms; only the timestamp fed to the original predicates
        // uses the CLI's floating-point clipping (a few ulps at these settings).
        const double boundary = (step / kSampleSteps + 1) * session.cfg.outputStep;
        for (int n = 0; n < count; ++n)
            session.Advance(kStep, std::min(kStep, boundary - session.t));
        return count;
    } catch (...) { return -1; }
}

int32_t Spr7_Read(void* handle, Spr7Frame* frame, int32_t frameBytes) {
    if (!handle || !frame || frameBytes != sizeof(Spr7Frame)) return -1;
    try {
        auto& session = *static_cast<Session*>(handle);
        if (session.step % kSampleSteps) return -1;
        const auto& state = session.model.GetState();
        const auto ports = session.model.ReadPortReferenceMotion();
        const auto joints = session.model.ArmJointFrames(state);
        const auto tool = session.model.ArmTool(state);
        const auto diagnostics = session.model.Inspect(state);
        const auto& events = session.collision.Events();
        frame->time = session.step * kStep;
        frame->mode = static_cast<double>(session.model.GetMode());
        Copy(frame->q, state.q);
        Copy(frame->u, state.u);
        Pose(frame->stationPort, ports.stationPosition, ports.stationQuaternion);
        Pose(frame->visitorPort, ports.visitorPosition, ports.visitorQuaternion);
        Copy(frame->stationPortVelocity, ports.stationVelocity);
        Copy(frame->visitorPortVelocity, ports.visitorVelocity);
        for (int i = 0; i < 7; ++i)
            Pose(frame->jointFrames[i], joints[i].position,
                DockingDynamics::RotationToQuaternion(joints[i].rotation));
        Pose(frame->tool, tool.position, DockingDynamics::RotationToQuaternion(tool.rotation));
        Copy(frame->grapple, diagnostics.visitorGrapplePosition);
        frame->portDistance = (ports.visitorPosition - ports.stationPosition).norm();
        frame->centerDistance = (state.q.segment<3>(32) - state.q.head<3>()).norm();
        frame->axialGap = diagnostics.axialGap;
        frame->toolDistance = diagnostics.armPositionError;
        frame->events[0] = events.firstArmTouch;
        frame->events[1] = session.armCapture;
        frame->events[2] = events.firstStationTouch;
        frame->events[3] = session.firstLockRequest;
        frame->events[4] = session.firstLock;
        frame->events[5] = session.release;
        frame->lockRequestSetting = session.cfg.controller.armStationLockRequestTime;
        return 0;
    } catch (...) { return -1; }
}


