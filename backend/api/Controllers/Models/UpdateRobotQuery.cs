﻿namespace Api.Database.Models
{
    public class UpdateRobotQuery
    {
        public string? InspectionAreaId { get; set; }

        public Pose? Pose { get; set; }

        public string? MissionId { get; set; }
    }
}
