# ADR-002: Azure runtime and portable telemetry

Status: Accepted (initial foundation)

Decision: React SPA on Azure Static Web Apps; ASP.NET Core API Linux container on Azure App Service using Azure Container Registry; PostgreSQL; Azure Blob Storage; Key Vault; Application Insights; Foundry. Local Compose uses Azurite and Grafana LGTM. OpenTelemetry/ILogger instrumentation via dependency injection chooses local OTLP or Azure Monitor.

Consequences: No vendor dependency in domain. Cloud services cost money and require credentials. No VNet by current product request, so apply strict secret hygiene and public endpoint hardening. Migrations will require isolated durable workers beyond this initial API.
