using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Po.Community.Core.Extensions;
using Po.Community.Core.Models;
using Po.Community.Core.Utilities;

namespace Po.Community.Core.McpTools;

public class MedicationRisksTool : IMcpTool
{
    private const string PatientIdParameter = "patientId";

    public string Name { get; } = "CheckMedicationRisks";

    public string? Description { get; } =
        "Checks a patient's medications for simple safety risks such as allergy conflicts, duplicate therapies, and polypharmacy.";

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

        var medicationBundle = await fhirClient.SearchAsync<MedicationRequest>(
            new[] { $"patient={patientId}" }
        );

        var allergyBundle = await fhirClient.SearchAsync<AllergyIntolerance>(
            new[] { $"patient={patientId}" }
        );

        var medications = medicationBundle?.Entry?
            .Select(e => e.Resource as MedicationRequest)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList() ?? [];

        var allergies = allergyBundle?.Entry?
            .Select(e => e.Resource as AllergyIntolerance)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList() ?? [];

        var medicationNames = medications
            .Select(GetMedicationName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        var allergyNames = allergies
            .Select(GetAllergyName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        var risks = BuildRisks(medicationNames, allergyNames);

        var officialName = patient.Name?.FirstOrDefault();
        var fullName = officialName is null
            ? "Unknown"
            : $"{string.Join(" ", officialName.Given ?? []).Trim()} {officialName.Family}".Trim();

        var summary = risks.Count == 0
            ? $"{fullName} has no obvious medication safety risks based on the available data."
            : $"{fullName} has {risks.Count} potential medication risk(s) that may need review.";

        var result =
            $"""
            Medication Risk Check
            ---------------------
            Patient ID: {patient.Id}
            Name: {fullName}

            Current Medications:
            {(medicationNames.Count == 0 ? "- None found" : string.Join(Environment.NewLine, medicationNames.Select(m => $"- {m}")))}

            Recorded Allergies:
            {(allergyNames.Count == 0 ? "- None found" : string.Join(Environment.NewLine, allergyNames.Select(a => $"- {a}")))}

            Potential Risks:
            {(risks.Count == 0 ? "- None identified" : string.Join(Environment.NewLine, risks.Select(r => $"- {r}")))}

            Summary:
            {summary}
            """;

        return McpToolUtilities.CreateTextToolResponse(result);
    }

    private static List<string> BuildRisks(List<string> medicationNames, List<string> allergyNames)
    {
        var risks = new List<string>();

        var medsLower = medicationNames.Select(m => m.ToLowerInvariant()).ToList();
        var allergiesLower = allergyNames.Select(a => a.ToLowerInvariant()).ToList();

        if (medicationNames.Count >= 5)
        {
            risks.Add("Polypharmacy risk: patient is taking 5 or more medications.");
        }

        if (HasAllergyConflict(medsLower, allergiesLower, "penicillin", ["amoxicillin", "ampicillin", "penicillin"]))
        {
            risks.Add("Possible allergy conflict: penicillin allergy with a penicillin-class medication.");
        }

        if (HasAllergyConflict(medsLower, allergiesLower, "sulfa", ["sulfamethoxazole", "tmp-smx", "bactrim"]))
        {
            risks.Add("Possible allergy conflict: sulfa allergy with a sulfonamide medication.");
        }

        if (ContainsBoth(medsLower, "warfarin", "aspirin"))
        {
            risks.Add("Possible interaction: warfarin and aspirin may increase bleeding risk.");
        }

        if (ContainsBoth(medsLower, "lisinopril", "losartan"))
        {
            risks.Add("Possible duplicate/overlapping therapy: lisinopril and losartan.");
        }

        if (HasDuplicateMedicationNames(medicationNames))
        {
            risks.Add("Possible duplicate medication entries found.");
        }

        return risks;
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

    private static bool ContainsBoth(List<string> medsLower, string med1, string med2)
    {
        return medsLower.Any(m => m.Contains(med1)) && medsLower.Any(m => m.Contains(med2));
    }

    private static bool HasDuplicateMedicationNames(List<string> medicationNames)
    {
        return medicationNames
            .Select(m => m.Trim().ToLowerInvariant())
            .GroupBy(m => m)
            .Any(g => g.Count() > 1);
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