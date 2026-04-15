using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Po.Community.Core.Extensions;
using Po.Community.Core.Models;
using Po.Community.Core.Utilities;

namespace Po.Community.Core.McpTools;

public class RecommendNextStepsTool : IMcpTool
{
    private const string PatientIdParameter = "patientId";

    public string Name { get; } = "RecommendNextSteps";

    public string? Description { get; } =
        "Recommends practical next steps for a patient based on conditions, medication risks, and possible care gaps.";

    public List<McpToolArgument> Arguments { get; } =
    [
        new McpToolArgument
        {
            Type = "string",
            Name = PatientIdParameter,
            Description = "The patient's id. Optional if patient context already exists.",
            IsRequired = false,
        },
    ];

    public List<string> FhirScopes { get; } =
    [
        "patient/Patient.read",
        "patient/Condition.read",
        "patient/Observation.read",
        "patient/Encounter.read",
        "patient/MedicationRequest.read",
        "patient/AllergyIntolerance.read"
    ];

    public async Task<CallToolResult> HandleAsync(
        HttpContext httpContext,
        McpServer mcpServer,
        IServiceProvider serviceProvider,
        CallToolRequestParams context
    )
    {
        var patientId = httpContext.GetPatientIdIfContextExists();

        if (string.IsNullOrWhiteSpace(patientId))
        {
            patientId = context.GetRequiredArgumentValue(PatientIdParameter);
        }

        var fhirClient = httpContext.CreateFhirClientWithContext();

        var patient = await fhirClient.ReadAsync<Patient>($"Patient/{patientId}");
        if (patient is null)
        {
            return McpToolUtilities.CreateTextToolResponse(
                "The patient could not be found.",
                isError: true
            );
        }

        var conditionsBundle = await fhirClient.SearchAsync<Condition>(new[] { $"patient={patientId}" });
        var observationsBundle = await fhirClient.SearchAsync<Observation>(new[] { $"patient={patientId}" });
        var encountersBundle = await fhirClient.SearchAsync<Encounter>(new[] { $"patient={patientId}" });
        var medicationsBundle = await fhirClient.SearchAsync<MedicationRequest>(new[] { $"patient={patientId}" });
        var allergiesBundle = await fhirClient.SearchAsync<AllergyIntolerance>(new[] { $"patient={patientId}" });

        var conditionNames = conditionsBundle?.Entry?
            .Select(e => e.Resource as Condition)
            .Where(c => c is not null)
            .Select(c => c!.Code?.Text ?? c!.Code?.Coding?.FirstOrDefault()?.Display ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList() ?? [];

        var observations = observationsBundle?.Entry?
            .Select(e => e.Resource as Observation)
            .Where(o => o is not null)
            .Select(o => o!)
            .ToList() ?? [];

        var encounters = encountersBundle?.Entry?
            .Select(e => e.Resource as Encounter)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList() ?? [];

        var medicationNames = medicationsBundle?.Entry?
            .Select(e => e.Resource as MedicationRequest)
            .Where(m => m is not null)
            .Select(m => GetMedicationName(m!))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList() ?? [];

        var allergyNames = allergiesBundle?.Entry?
            .Select(e => e.Resource as AllergyIntolerance)
            .Where(a => a is not null)
            .Select(a => GetAllergyName(a!))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList() ?? [];

        var recommendations = BuildRecommendations(
            patient,
            conditionNames,
            observations,
            encounters,
            medicationNames,
            allergyNames
        );

        var officialName = patient.Name?.FirstOrDefault();
        var fullName = officialName is null
            ? "Unknown"
            : $"{string.Join(" ", officialName.Given ?? []).Trim()} {officialName.Family}".Trim();

        var result =
            $"""
            Recommended Next Steps
            ----------------------
            Patient ID: {patient.Id}
            Name: {fullName}

            Recommendations:
            {(recommendations.Count == 0
                ? "- No obvious next steps identified from the available data."
                : string.Join(Environment.NewLine, recommendations.Select(r => $"- {r}")))}
            """;

        return McpToolUtilities.CreateTextToolResponse(result);
    }

    private static List<string> BuildRecommendations(
        Patient patient,
        List<string> conditionNames,
        List<Observation> observations,
        List<Encounter> encounters,
        List<string> medicationNames,
        List<string> allergyNames
    )
    {
        var recommendations = new List<string>();

        var hasDiabetes = conditionNames.Any(c =>
            c.Contains("diabetes", StringComparison.OrdinalIgnoreCase));

        var hasHypertension = conditionNames.Any(c =>
            c.Contains("hypertension", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("high blood pressure", StringComparison.OrdinalIgnoreCase));

        if (hasDiabetes)
        {
            var latestA1c = observations
                .Where(IsA1cObservation)
                .OrderByDescending(GetObservationDate)
                .FirstOrDefault();

            if (latestA1c is null || IsOlderThanMonths(GetObservationDate(latestA1c), 6))
            {
                recommendations.Add("Order or schedule an A1C test.");
            }

            recommendations.Add("Review diabetes management and glucose control at the next follow-up.");
        }

        if (hasHypertension)
        {
            var latestBp = observations
                .Where(IsBloodPressureObservation)
                .OrderByDescending(GetObservationDate)
                .FirstOrDefault();

            if (latestBp is null || IsOlderThanMonths(GetObservationDate(latestBp), 6))
            {
                recommendations.Add("Schedule a blood pressure follow-up visit.");
            }
        }

        var latestEncounter = encounters
            .OrderByDescending(GetEncounterDate)
            .FirstOrDefault();

        if (latestEncounter is null || IsOlderThanMonths(GetEncounterDate(latestEncounter), 12))
        {
            recommendations.Add("Schedule a primary care or annual wellness follow-up.");
        }

        if (medicationNames.Count >= 5)
        {
            recommendations.Add("Perform a medication review for possible polypharmacy.");
        }

        var medsLower = medicationNames.Select(m => m.ToLowerInvariant()).ToList();
        var allergiesLower = allergyNames.Select(a => a.ToLowerInvariant()).ToList();

        if (ContainsBoth(medsLower, "warfarin", "aspirin"))
        {
            recommendations.Add("Review possible bleeding risk from warfarin and aspirin combination.");
        }

        if (HasAllergyConflict(medsLower, allergiesLower, "penicillin", ["amoxicillin", "ampicillin", "penicillin"]))
        {
            recommendations.Add("Review medication list for possible penicillin allergy conflict.");
        }

        if (ContainsBoth(medsLower, "lisinopril", "losartan"))
        {
            recommendations.Add("Review overlapping blood pressure therapy.");
        }

        return recommendations.Distinct().ToList();
    }

    private static bool IsA1cObservation(Observation observation)
    {
        return (observation.Code?.Text?.Contains("A1C", StringComparison.OrdinalIgnoreCase) ?? false)
            || (observation.Code?.Text?.Contains("hemoglobin a1c", StringComparison.OrdinalIgnoreCase) ?? false)
            || (observation.Code?.Coding?.Any(c =>
                (c.Display?.Contains("A1C", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (c.Display?.Contains("hemoglobin a1c", StringComparison.OrdinalIgnoreCase) ?? false)
            ) ?? false);
    }

    private static bool IsBloodPressureObservation(Observation observation)
    {
        return (observation.Code?.Text?.Contains("blood pressure", StringComparison.OrdinalIgnoreCase) ?? false)
            || (observation.Code?.Coding?.Any(c =>
                c.Display?.Contains("blood pressure", StringComparison.OrdinalIgnoreCase) ?? false
            ) ?? false);
    }

    private static DateTimeOffset? GetObservationDate(Observation observation)
    {
        if (observation.Effective is FhirDateTime fhirDateTime &&
            DateTimeOffset.TryParse(fhirDateTime.Value, out var parsedDateTime))
        {
            return parsedDateTime;
        }

        return observation.Issued;
    }

    private static DateTimeOffset? GetEncounterDate(Encounter encounter)
    {
        if (encounter.Period?.StartElement is not null &&
            DateTimeOffset.TryParse(encounter.Period.StartElement.Value, out var parsedStart))
        {
            return parsedStart;
        }

        return null;
    }

    private static bool IsOlderThanMonths(DateTimeOffset? date, int months)
    {
        if (date is null) return true;
        return date.Value < DateTimeOffset.UtcNow.AddMonths(-months);
    }

    private static bool ContainsBoth(List<string> medsLower, string med1, string med2)
    {
        return medsLower.Any(m => m.Contains(med1)) && medsLower.Any(m => m.Contains(med2));
    }

    private static bool HasAllergyConflict(
        List<string> medsLower,
        List<string> allergiesLower,
        string allergyKeyword,
        List<string> relatedMeds
    )
    {
        var hasAllergy = allergiesLower.Any(a => a.Contains(allergyKeyword));
        var hasRelatedMedication = medsLower.Any(m => relatedMeds.Any(r => m.Contains(r)));
        return hasAllergy && hasRelatedMedication;
    }

    private static string GetMedicationName(MedicationRequest medicationRequest)
    {
        return medicationRequest.Medication is CodeableConcept codeableConcept
            ? codeableConcept.Text
                ?? codeableConcept.Coding?.FirstOrDefault()?.Display
                ?? "Unknown medication"
            : "Unknown medication";
    }

    private static string GetAllergyName(AllergyIntolerance allergy)
    {
        return allergy.Code?.Text
            ?? allergy.Code?.Coding?.FirstOrDefault()?.Display
            ?? "Unknown allergy";
    }
}