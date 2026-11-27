#pragma once
#include "Controller.hpp"
#include "ExternalContactDemo.hpp"
#include "ExternalPlumeDemo.hpp"
#include "ExternalSeparationDemo.hpp"
#include <cstdint>

namespace docking::demo {
struct SimulationSettings {
  DemoScenario scenario=DemoScenario::StationPort;
  double durationSeconds=36.0;
  double integrationStep=0.001;    // RK4 internal step; reduce for convergence.
  double outputStep=0.02;         // CSV sample period, independent of internal RK4 step.
  double holdSeconds=6.0;
  std::string csvFile="results_station.csv";
  bool enableCollision=true,enablePlume=true,enableSeparation=true;
  bool quiet=false,selfTest=false,probe=false;
  StationLockModel lockModel=StationLockModel::RigidComposite; // one actual spacecraft when latched

  // All vectors below are configurable in main(). Local port/grapple coordinates
  // are BODY-FRAME offsets (not absolute world points).
  Vec3 stationInitialPosition,visitorInitialPosition,visitorInitialVelocity;
  Vec4 stationInitialQuaternion,visitorInitialQuaternion;
  Vec3 stationPort,visitorPort,stationPortNormal,visitorGrapple;
  Vec3 armMount,panelLeftMount,panelRightMount;
  bool autoVisitorStartNearArm=true;  // If arm scenario, compute incoming trajectory from tool pose.
  double armApproachStandoff=0.33,armApproachSpeed=0.06;
  ContactModelSettings collision;
  SeparationSettings separation;
  ControllerSettings controller;
};

inline DockParameters makeParameters(const SimulationSettings& cfg){
 DockParameters p=MakeDefaultParameters(); // preserves the seven-joint, new-FE and panel data.
 p.lockModel=cfg.lockModel;
 p.outputStep=cfg.outputStep;
 p.rk4Substeps=1; // main controls the actual internal RK4 steps.
 p.initialStationPosition=cfg.stationInitialPosition;
 p.initialStationQuat=cfg.stationInitialQuaternion;
 p.initialVisitorPosition=cfg.visitorInitialPosition;
 p.initialVisitorVelocity=cfg.visitorInitialVelocity;
 p.initialVisitorQuat=cfg.visitorInitialQuaternion;
 p.armMount=cfg.armMount;
 p.panelLeftMount=cfg.panelLeftMount;
 p.panelRightMount=cfg.panelRightMount;
 p.portStation=cfg.stationPort;
 p.portVisitor=cfg.visitorPort;
 p.stationPortNormalLocal=cfg.stationPortNormal.normalized();
 p.visitorGrapple=cfg.visitorGrapple;
 p.stationContactSkin=cfg.collision.stationSkin;
 p.armContactSkin=cfg.collision.armSkin;
 p.stationContactLateralTol=cfg.collision.stationLateralTolerance;
 p.armContactLateralTol=cfg.collision.armLateralTolerance;
 if(cfg.scenario==DemoScenario::ArmCapture){
   p.initialJointAngles=cfg.controller.qCapture;
   if(cfg.autoVisitorStartNearArm){
     // Optional example auto-placement; switch off to use the explicitly set
     // visitorInitialPosition and visitorInitialVelocity in main().
     DockingDynamics toolSolver(p);
     const ToolPose T=toolSolver.ArmTool(toolSolver.GetState());
     const Vec3 normal=T.rotation*Vec3::UnitZ();
     const Mat3 RV=DockingDynamics::QuaternionToRotation(p.initialVisitorQuat);
     p.initialVisitorPosition=T.position+cfg.armApproachStandoff*normal-RV*p.visitorGrapple;
     p.initialVisitorVelocity=-cfg.armApproachSpeed*normal;
   }
 }
 return p;
}

inline SimulationSettings DefaultSettings() {
   SimulationSettings cfg;
   cfg.scenario=DemoScenario::StationPort; 

   //cfg.scenario = DemoScenario::ArmCapture;// change to ArmCapture for arm assist
   cfg.integrationStep=0.001;  // s, RK4 integration (use 0.0005 for convergence check)
   cfg.outputStep=0.02;       // s, CSV sampling period
   cfg.durationSeconds=(cfg.scenario==DemoScenario::ArmCapture?80.:36.); // s
   cfg.holdSeconds=6.0;       // locked dwell time before external release
   cfg.lockModel=StationLockModel::RigidComposite; // one combined rigid spacecraft on latch
   // Edit HERE to choose the EARLIEST station-port lock signal time (seconds).
   // The existing physical contact/geometry/speed gates are preserved; the
   // actual latch happens immediately once a VALID signal is accepted.
   // Direct-port docking scenario:
   cfg.controller.lockRequestTime=18.20;
   // 7-DOF arm-assisted scenario (the selected default below):
   cfg.controller.armStationLockRequestTime=58.00;
   cfg.csvFile=(cfg.scenario==DemoScenario::ArmCapture?
                 "results_arm.csv":"results_station.csv");

   // Inertial initial states (m, m/s, quaternion in [w,x,y,z] order).
   cfg.stationInitialPosition=Vec3(0,0,0);
   cfg.stationInitialQuaternion=Vec4(1,0,0,0);
   cfg.visitorInitialPosition=Vec3(4.3,0,0);
   cfg.visitorInitialVelocity=Vec3(-0.1,0,0);
   cfg.visitorInitialQuaternion=Vec4(1,0,0,0);

   // BODY-FRAME docking hardware positions; changing the spacecraft layout
   // requires editing only these geometry values, NOT DockingDynamics.cpp.
   cfg.stationPort=Vec3(1.25,0,0);
   cfg.visitorPort=Vec3(-1.25,0,0);
   cfg.stationPortNormal=Vec3(1,0,0); // station docking face outward normal
   cfg.visitorGrapple=Vec3(0,0,1);
   cfg.armMount=Vec3(1,0,2);
   cfg.panelLeftMount=Vec3(0,2,-2);
   cfg.panelRightMount=Vec3(0,-2,-2);

   // Arm scenario: TRUE keeps incoming visitor on a straight line aimed at
   // the nominal tool. FALSE uses the manual visitor positions above.
   cfg.autoVisitorStartNearArm=true;
   cfg.armApproachStandoff=0.33;
   cfg.armApproachSpeed=0.060;

   // Independent EXTERNAL load switches, not a part of dynamics equations.
   cfg.enableCollision=true;
   cfg.enablePlume=true;
   cfg.enableSeparation=true;

   return cfg;
}

// Shared scenario driver used by the native CLI and the Unity session DLL.
class Session {
public:
 explicit Session(const SimulationSettings& settings)
     : cfg(settings), model(makeParameters(cfg)), collision(cfg.collision),
       control(cfg.controller), separator(cfg.separation) {}

 void Advance(double dt, double clockAdvance) {
   const State& x=model.GetState();
   const DockMode previous=model.GetMode();
   const DockDiagnostics d=model.Inspect(x);
   in=Inputs{};
   in.control=control.Calculate(t,x,previous,model.Parameters(),cfg.scenario);
   if(firstLockRequest<0 && in.control.requestLock)firstLockRequest=t;
   if(firstLock>=0 && t>=firstLock+cfg.holdSeconds &&
       (previous==DockMode::StationLocked||previous==DockMode::ArmCaptured))
     in.control.requestRelease=true;
   if(cfg.enablePlume)plume.Evaluate(t,x,in.loads.plume);
   if(cfg.enableCollision)
     collision.Evaluate(t,model,x,d,previous,cfg.scenario,true,false,in);
   else {
     in.control.requestContact=false;
     in.control.requestLock=false;
     in.control.requestArmCapture=false;
   }
   if(cfg.enableSeparation && previous==DockMode::Separated)
     separator.Evaluate(model,x,d,in);
   model.AdvanceFast(in,dt);
   t+=clockAdvance;
   ++step;
   const DockMode mode=model.GetMode();
   if(mode!=previous) {
     collision.RegisterModeChange(t,mode);
     if(mode==DockMode::ArmCaptured)armCapture=t;
     if(mode==DockMode::StationLocked)firstLock=t;
     if(mode==DockMode::Separated)release=t;
   }
 }

 const SimulationSettings cfg;
 DockingDynamics model;
 Inputs in;
 double t=0.,firstLock=-1.,armCapture=-1.,release=-1.,firstLockRequest=-1.;
 std::uint32_t step=0;
 ExternalContactDemo collision;

private:
 DockingPDController control;
 ExternalPlumeDemo plume;
 ExternalSeparationDemo separator;
};
} // namespace docking::demo


