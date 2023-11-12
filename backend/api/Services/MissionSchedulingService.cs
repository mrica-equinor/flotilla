﻿using System.Text.Json;
using Api.Controllers;
using Api.Controllers.Models;
using Api.Database.Models;
using Api.Services.Events;
using Api.Utilities;
using Microsoft.AspNetCore.Mvc;
namespace Api.Services
{
    public interface IMissionSchedulingService
    {
        public void StartMissionRunIfSystemIsAvailable(MissionRun missionRun);

        public Task<bool> OngoingMission(string robotId);

        public Task FreezeMissionRunQueueForRobot(string robotId);

        public Task StopCurrentMissionRun(string robotId);

        public Task ScheduleMissionToReturnToSafePosition(string robotId, string areaId);

        public Task UnfreezeMissionRunQueueForRobot(string robotId);

        public bool MissionRunQueueIsEmpty(IList<MissionRun> missionRunQueue);

        public void TriggerRobotAvailable(RobotAvailableEventArgs e);
    }

    public class MissionSchedulingService : IMissionSchedulingService
    {
        private readonly IAreaService _areaService;
        private readonly IIsarService _isarService;
        private readonly ILogger<MissionSchedulingService> _logger;
        private readonly IMissionRunService _missionRunService;
        private readonly RobotController _robotController;
        private readonly IRobotService _robotService;


        public MissionSchedulingService(ILogger<MissionSchedulingService> logger, IMissionRunService missionRunService, IRobotService robotService, RobotController robotController,
            IAreaService areaService, IIsarService isarService)
        {
            _logger = logger;
            _missionRunService = missionRunService;
            _robotService = robotService;
            _robotController = robotController;
            _areaService = areaService;
            _isarService = isarService;
        }

        public void StartMissionRunIfSystemIsAvailable(MissionRun missionRun)
        {
            if (!TheSystemIsAvailableToRunAMission(missionRun.Robot, missionRun).Result)
            {
                _logger.LogInformation("Mission {MissionRunId} was put on the queue as the system may not start a mission now", missionRun.Id);
                return;
            }

            try { StartMissionRun(missionRun); }
            catch (MissionException ex)
            {
                const MissionStatus NewStatus = MissionStatus.Failed;
                _logger.LogWarning(
                    "Mission run {MissionRunId} was not started successfully. Status updated to '{Status}'.\nReason: {FailReason}",
                    missionRun.Id,
                    NewStatus,
                    ex.Message
                );
                missionRun.Status = NewStatus;
                missionRun.StatusReason = $"Failed to start: '{ex.Message}'";
                _missionRunService.Update(missionRun);
            }
        }

        public async Task<bool> OngoingMission(string robotId)
        {
            var ongoingMissions = await GetOngoingMissions(robotId);
            return ongoingMissions is not null && ongoingMissions.Any();
        }


        public async Task FreezeMissionRunQueueForRobot(string robotId)
        {
            await _robotService.UpdateMissionQueueFrozen(robotId, true);
            _logger.LogInformation("Mission queue was frozen for robot with Id {RobotId}", robotId);
        }

        public async Task UnfreezeMissionRunQueueForRobot(string robotId)
        {
            await _robotService.UpdateMissionQueueFrozen(robotId, false);
            _logger.LogInformation("Mission queue for robot with ID {RobotId} was unfrozen", robotId);
        }

        public async Task StopCurrentMissionRun(string robotId)
        {
            var robot = await _robotService.ReadById(robotId);
            if (robot == null)
            {
                string errorMessage = $"Robot with ID: {robotId} was not found in the database";
                _logger.LogError("{Message}", errorMessage);
                throw new RobotNotFoundException(errorMessage);
            }

            var ongoingMissionRuns = await GetOngoingMissions(robotId);
            if (ongoingMissionRuns is null)
            {
                string errorMessage = $"There were no ongoing mission runs to stop for robot {robotId}";
                _logger.LogWarning("{Message}", errorMessage);
                throw new MissionRunNotFoundException(errorMessage);
            }

            IList<string> ongoingMissionRunIds = ongoingMissionRuns.Select(missionRun => missionRun.Id).ToList();

            try { await _isarService.StopMission(robot); }
            catch (HttpRequestException e)
            {
                const string Message = "Error connecting to ISAR while stopping mission";
                _logger.LogError(e, "{Message}", Message);
                OnIsarUnavailable(robot.Id);
                throw new MissionException(Message, (int)e.StatusCode!);
            }
            catch (MissionException e)
            {
                const string Message = "Error while stopping ISAR mission";
                _logger.LogError(e, "{Message}", Message);
                throw;
            }
            catch (JsonException e)
            {
                const string Message = "Error while processing the response from ISAR";
                _logger.LogError(e, "{Message}", Message);
                throw new MissionException(Message, 0);
            }
            catch (MissionNotFoundException) { _logger.LogWarning("{Message}", $"No mission was running for robot {robot.Id}"); }

            await MoveInterruptedMissionsToQueue(ongoingMissionRunIds);

            try { await _robotService.UpdateCurrentMissionId(robotId, null); }
            catch (RobotNotFoundException) { }
        }

        public async Task ScheduleMissionToReturnToSafePosition(string robotId, string areaId)
        {
            var area = await _areaService.ReadById(areaId);
            if (area == null)
            {
                _logger.LogError("Could not find area with ID {AreaId}", areaId);
                return;
            }
            var robot = await _robotService.ReadById(robotId);
            if (robot == null)
            {
                _logger.LogError("Robot with ID: {RobotId} was not found in the database", robotId);
                return;
            }
            var closestSafePosition = ClosestSafePosition(robot.Pose, area.SafePositions);
            // Cloning to avoid tracking same object
            var clonedPose = ObjectCopier.Clone(closestSafePosition);
            var customTaskQuery = new CustomTaskQuery
            {
                RobotPose = clonedPose,
                Inspections = new List<CustomInspectionQuery>(),
                InspectionTarget = new Position(),
                TaskOrder = 0
            };

            var missionRun = new MissionRun
            {
                Name = "Drive to Safe Position",
                Robot = robot,
                MissionRunPriority = MissionRunPriority.Emergency,
                InstallationCode = area.Installation.InstallationCode,
                Area = area,
                Status = MissionStatus.Pending,
                DesiredStartTime = DateTime.UtcNow,
                Tasks = new List<MissionTask>(new[]
                {
                    new MissionTask(customTaskQuery)
                }),
                Map = new MapMetadata()
            };

            await _missionRunService.Create(missionRun);
        }

        public bool MissionRunQueueIsEmpty(IList<MissionRun> missionRunQueue)
        {
            return !missionRunQueue.Any();
        }

        public void TriggerRobotAvailable(RobotAvailableEventArgs e)
        {
            OnRobotAvailable(e);
        }
        private async Task MoveInterruptedMissionsToQueue(IEnumerable<string> interruptedMissionRunIds)
        {
            foreach (string missionRunId in interruptedMissionRunIds)
            {
                var missionRun = await _missionRunService.ReadById(missionRunId);
                if (missionRun is null)
                {
                    _logger.LogWarning("{Message}", $"Interrupted mission run with Id {missionRunId} could not be found");
                    continue;
                }

                var newMissionRun = new MissionRun
                {
                    Name = missionRun.Name,
                    Robot = missionRun.Robot,
                    MissionRunPriority = missionRun.MissionRunPriority,
                    InstallationCode = missionRun.Area!.Installation.InstallationCode,
                    Area = missionRun.Area,
                    Status = MissionStatus.Pending,
                    DesiredStartTime = DateTime.UtcNow,
                    Tasks = missionRun.Tasks,
                    Map = new MapMetadata()
                };

                await _missionRunService.Create(newMissionRun);
            }
        }

        private void StartMissionRun(MissionRun queuedMissionRun)
        {
            var result = _robotController.StartMission(
                queuedMissionRun.Robot.Id,
                queuedMissionRun.Id
            ).Result;
            if (result.Result is not OkObjectResult)
            {
                string errorMessage = "Unknown error from robot controller";
                if (result.Result is ObjectResult returnObject)
                {
                    errorMessage = returnObject.Value?.ToString() ?? errorMessage;
                }
                throw new MissionException(errorMessage);
            }
            _logger.LogInformation("Started mission run '{Id}'", queuedMissionRun.Id);
        }

        private async void OnIsarUnavailable(string robotId)
        {
            var robot = await _robotService.ReadById(robotId);
            if (robot == null)
            {
                _logger.LogError("Robot with ID: {RobotId} was not found in the database", robotId);
                return;
            }

            if (robot.CurrentMissionId != null)
            {
                var missionRun = await _missionRunService.ReadById(robot.CurrentMissionId);
                if (missionRun != null)
                {
                    missionRun.SetToFailed();
                    await _missionRunService.Update(missionRun);
                    _logger.LogWarning(
                        "Mission '{Id}' failed because ISAR could not be reached",
                        missionRun.Id
                    );
                }
            }

            try
            {
                await _robotService.UpdateRobotStatus(robot.Id, RobotStatus.Offline);
                await _robotService.UpdateCurrentMissionId(robot.Id, null);
                await _robotService.UpdateRobotEnabled(robot.Id, false);
            }
            catch (RobotNotFoundException) { }
        }

        private static Pose ClosestSafePosition(Pose robotPose, IList<SafePosition> safePositions)
        {
            if (safePositions == null || !safePositions.Any())
            {
                string message = "No safe position for area the robot is localized in";
                throw new SafeZoneException(message);
            }

            var closestPose = safePositions[0].Pose;
            float minDistance = CalculateDistance(robotPose, closestPose);

            for (int i = 1; i < safePositions.Count; i++)
            {
                float currentDistance = CalculateDistance(robotPose, safePositions[i].Pose);
                if (currentDistance < minDistance)
                {
                    minDistance = currentDistance;
                    closestPose = safePositions[i].Pose;
                }
            }
            return closestPose;
        }

        public async Task<bool> TheSystemIsAvailableToRunAMission(string robotId, MissionRun missionRun)
        {
            var robot = await _robotService.ReadById(robotId);
            if (robot != null) { return await TheSystemIsAvailableToRunAMission(robot, missionRun); }

            _logger.LogError("Robot with ID: {RobotId} was not found in the database", robotId);
            return false;
        }

        private async Task<PagedList<MissionRun>?> GetOngoingMissions(string robotId)
        {
            var ongoingMissions = await _missionRunService.ReadAll(
                new MissionRunQueryStringParameters
                {
                    Statuses = new List<MissionStatus>
                    {
                        MissionStatus.Ongoing
                    },
                    RobotId = robotId,
                    OrderBy = "DesiredStartTime",
                    PageSize = 100
                });

            return ongoingMissions;
        }

        private async Task<bool> TheSystemIsAvailableToRunAMission(Robot robot, MissionRun missionRun)
        {
            bool ongoingMission = await OngoingMission(robot.Id);

            if (robot.MissionQueueFrozen && missionRun.MissionRunPriority != MissionRunPriority.Emergency)
            {
                _logger.LogInformation("Mission run {MissionRunId} was not started as the mission run queue for robot {RobotName} is frozen", missionRun.Id, robot.Name);
                return false;
            }

            if (ongoingMission)
            {
                _logger.LogInformation("Mission run {MissionRunId} was not started as there is already an ongoing mission", missionRun.Id);
                return false;
            }
            if (robot.Status is not RobotStatus.Available)
            {
                _logger.LogInformation("Mission run {MissionRunId} was not started as the robot is not available", missionRun.Id);
                return false;
            }
            if (!robot.Enabled)
            {
                _logger.LogWarning("Mission run {MissionRunId} was not started as the robot {RobotId} is not enabled", missionRun.Id, robot.Id);
                return false;
            }
            return true;
        }

        private static float CalculateDistance(Pose pose1, Pose pose2)
        {
            var pos1 = pose1.Position;
            var pos2 = pose2.Position;
            return (float)Math.Sqrt(Math.Pow(pos1.X - pos2.X, 2) + Math.Pow(pos1.Y - pos2.Y, 2) + Math.Pow(pos1.Z - pos2.Z, 2));
        }
        protected virtual void OnRobotAvailable(RobotAvailableEventArgs e) { RobotAvailable?.Invoke(this, e); }
        public static event EventHandler<RobotAvailableEventArgs>? RobotAvailable;
    }
}
