﻿using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Api.Controllers.Models;
using Api.Database.Context;
using Api.Database.Models;
using Api.Services.Models;
using Api.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Abstractions;

namespace Api.Services
{
    public interface IInspectionService
    {
        public Task<byte[]?> FetchInpectionImageFromIsarInspectionId(string isarInspectionId);
        public Task<Inspection> UpdateInspectionStatus(
            string isarTaskId,
            IsarTaskStatus isarTaskStatus
        );
        public Task<Inspection?> ReadByIsarTaskId(string id, bool readOnly = true);
        public Task<Inspection?> AddFinding(
            InspectionFindingQuery inspectionFindingsQuery,
            string isarTaskId
        );
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal StringComparison",
        Justification = "EF Core refrains from translating string comparison overloads to SQL"
    )]
    public class InspectionService(
        FlotillaDbContext context,
        ILogger<InspectionService> logger,
        IDownstreamApi idaApi,
        IAccessRoleService accessRoleService,
        IBlobService blobService
    ) : IInspectionService
    {
        public const string ServiceName = "IDA";

        public async Task<byte[]?> FetchInpectionImageFromIsarInspectionId(string isarInspectionId)
        {
            var inspectionData =
                await GetInspectionStorageInfo(isarInspectionId)
                ?? throw new InspectionNotFoundException(
                    $"Could not find inspection data for inspection with ISAR Inspection Id {isarInspectionId}."
                );
            return await blobService.DownloadBlob(
                inspectionData.BlobName,
                inspectionData.BlobContainer,
                inspectionData.StorageAccount
            );
        }

        public async Task<Inspection> UpdateInspectionStatus(
            string isarTaskId,
            IsarTaskStatus isarTaskStatus
        )
        {
            var inspection = await ReadByIsarTaskId(isarTaskId, readOnly: true);
            if (inspection is null)
            {
                string errorMessage = $"Inspection with task ID {isarTaskId} could not be found";
                logger.LogError("{Message}", errorMessage);
                throw new InspectionNotFoundException(errorMessage);
            }

            inspection.UpdateStatus(isarTaskStatus);
            inspection = await Update(inspection);
            return inspection;
        }

        private async Task ApplyDatabaseUpdate(Installation? installation)
        {
            var accessibleInstallationCodes = await accessRoleService.GetAllowedInstallationCodes();
            if (
                installation == null
                || accessibleInstallationCodes.Contains(
                    installation.InstallationCode.ToUpper(CultureInfo.CurrentCulture)
                )
            )
                await context.SaveChangesAsync();
            else
                throw new UnauthorizedAccessException(
                    $"User does not have permission to update area in installation {installation.Name}"
                );
        }

        private async Task<Inspection> Update(Inspection inspection)
        {
            var entry = context.Update(inspection);

            var missionRun = await context
                .MissionRuns.Include(missionRun => missionRun.InspectionArea)
                .ThenInclude(area => area != null ? area.Installation : null)
                .Include(missionRun => missionRun.Robot)
                .Where(missionRun =>
                    missionRun.Tasks.Any(missionTask =>
                        missionTask.Inspection != null && missionTask.Inspection.Id == inspection.Id
                    )
                )
                .AsNoTracking()
                .FirstOrDefaultAsync();
            var installation = missionRun?.InspectionArea?.Installation;

            await ApplyDatabaseUpdate(installation);
            DetachTracking(inspection);
        }

        public async Task<Inspection?> ReadByIsarTaskId(string id, bool readOnly = true)
        {
            return await GetInspections(readOnly: readOnly)
                .FirstOrDefaultAsync(inspection =>
                    inspection.IsarTaskId != null && inspection.IsarTaskId.Equals(id)
                );
        }

        private IQueryable<Inspection> GetInspections(bool readOnly = true)
        {
            if (accessRoleService.IsUserAdmin() || !accessRoleService.IsAuthenticationAvailable())
                return (
                    readOnly ? context.Inspections.AsNoTracking() : context.Inspections.AsTracking()
                ).Include(inspection => inspection.InspectionFindings);
            else
                throw new UnauthorizedAccessException(
                    $"User does not have permission to view inspections"
                );
        }

        public async Task<Inspection?> AddFinding(
            InspectionFindingQuery inspectionFindingQuery,
            string isarTaskId
        )
        {
            var inspection = await ReadByIsarTaskId(isarTaskId, readOnly: true);

            if (inspection is null)
            {
                return null;
            }

            var inspectionFinding = new InspectionFinding
            {
                InspectionDate = inspectionFindingQuery.InspectionDate.ToUniversalTime(),
                Finding = inspectionFindingQuery.Finding,
                IsarTaskId = isarTaskId,
            };

            inspection.InspectionFindings.Add(inspectionFinding);
            inspection = await Update(inspection);
            return inspection;
        }

        private async Task<IDAInspectionDataResponse?> GetInspectionStorageInfo(string inspectionId)
        {
            string relativePath = $"InspectionData/{inspectionId}/inspection-data-storage-location";

            HttpResponseMessage response;
            try
            {
                response = await idaApi.CallApiForAppAsync(
                    ServiceName,
                    options =>
                    {
                        options.HttpMethod = HttpMethod.Get.Method;
                        options.RelativePath = relativePath;
                    }
                );
            }
            catch (Exception e)
            {
                logger.LogError(e, "{ErrorMessage}", e.Message);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var inspectionData =
                    await response.Content.ReadFromJsonAsync<IDAInspectionDataResponse>()
                    ?? throw new JsonException("Failed to deserialize inspection data from IDA.");
                return inspectionData;
            }

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                logger.LogInformation(
                    "Inspection data storage location for inspection with Id {inspectionId} is not yet available",
                    inspectionId
                );
                return null;
            }

            if (response.StatusCode == HttpStatusCode.InternalServerError)
            {
                logger.LogError(
                    "Inetrnal server error when trying to get inspection data for inspection with Id {inspectionId}",
                    inspectionId
                );
                return null;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogError(
                    "Could not find inspection data for inspection with Id {inspectionId}",
                    inspectionId
                );
                return null;
            }

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                logger.LogError(
                    "Anonymization workflow failed for inspection with Id {inspectionId}",
                    inspectionId
                );
                return null;
            }

            logger.LogError(
                "Unexpected error when trying to get inspection data for inspection with Id {inspectionId}",
                inspectionId
            );
            return null;
        }

        public void DetachTracking(Inspection inspection)
        {
            foreach (var inspectionFinding in inspection.InspectionFindings)
            {
                context.Entry(inspectionFinding).State = EntityState.Detached;
            }
            context.Entry(inspection).State = EntityState.Detached;
        }
    }
}
