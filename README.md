# 🧠 Healthcare AI MCP Toolkit

![MCP](https://img.shields.io/badge/MCP-Enabled-blue)
![FHIR](https://img.shields.io/badge/FHIR-Integrated-green)
![Hackathon](https://img.shields.io/badge/Hackathon-Project-orange)

## 🚀 Overview

This project is a healthcare AI toolkit built using the **Model Context Protocol (MCP)**. It provides interoperable tools that transform raw patient data into meaningful clinical insights and actionable recommendations.

The system demonstrates how AI agents can support real-world healthcare workflows by:
- Understanding patient context  
- Identifying care gaps  
- Detecting medication risks  
- Recommending next steps  

---

## 🛠️ Tools

### 🧾 GetPatientSummary
Retrieves key patient information and provides a concise clinical overview, including demographics, conditions, and medications.

**Why it matters:**  
Establishes the foundation for understanding a patient before any analysis.

---

### 🩺 PatientCareGapsTool
Identifies potential gaps in a patient’s care by analyzing conditions, observations, and visit history.

**Detects:**
- Overdue tests (e.g., A1C)  
- Missing follow-ups  
- Preventive care needs  

**Why it matters:**  
Highlights what care is missing so providers can act proactively.

---

### 💊 CheckMedicationRisks
Analyzes medications and allergies to identify potential safety concerns.

**Detects:**
- Drug interactions  
- Allergy conflicts  
- Polypharmacy  

**Why it matters:**  
Supports safer clinical decisions by flagging risks early.

---

### 📋 RecommendNextSteps
Generates actionable recommendations based on patient data, care gaps, and medication risks.

**Examples:**
- Schedule follow-up visits  
- Order lab tests  
- Review medications  

**Why it matters:**  
Transforms insights into clear, practical actions.

---

## 🔗 How It Works

These tools are designed to work together:

```text
GetPatientSummary → PatientCareGapsTool → CheckMedicationRisks → RecommendNextSteps


# Overview

An open source repository where developers can add additional FHIR related tools to the default MCP server used by
[Prompt Opinion](https://promptopinion.ai).

## SHARP-on-MCP Specification

- [latest version](https://www.sharponmcp.com/)
