# Terraform: initial Azure environment

Resources: resource group, Log Analytics, Application Insights, ACR, Linux App Service (container), PostgreSQL Flexible Server + database, Storage Account + private blob container, Key Vault with RBAC, Static Web App (Standard) and Microsoft Foundry project. Model `gpt-6.1-sol` deployment is optionally enabled through variables.

> Warning: Phase-0 infrastructure; services are public without VNet at the user's request, including a broad Azure-services PostgreSQL firewall rule. Do not place production credentials or sensitive repositories on it until hardened.

## 1. Prerequisites

Azure login and tenant/subscription/role permissions, Terraform >=1.9, Azure CLI, .NET/Node build dependencies. Choose a secure REMOTE Terraform state backend before using real secrets. The PostgreSQL administrator password is still stored in Terraform state even with `sensitive = true`. You need quota and regional availability for Foundry.

```bash
cd infra/terraform
cp terraform.tfvars.example terraform.tfvars
# edit subscription_id/region
export TF_VAR_postgres_admin_password='<strong-secret>'
terraform init
terraform validate
terraform plan
terraform apply
```

## 2. Create secrets OUTSIDE Terraform

After creating Key Vault, set these secrets with `az keyvault secret set --vault-name <vault> --name <name> --value <value>` using a secure CI environment (avoid leaking shell history). Your deploy identity needs Key Vault Secrets Officer role. Key Vault secret names:

- `jwt-signing-key`: unpredictable, at least 32 UTF-8 bytes; different per environment.
- `bootstrap-password`: strong temporary superadmin password, not `superadmin`.
- `postgres-connection-string`: full Npgsql connection string, e.g. `Host=<fqdn>;Port=5432;Database=githubmigrator;Username=<admin>;Password=<secret>;SSL Mode=Require`.
- `storage-connection-string`: Azure Blob Storage connection string for created account.

Web App uses its own managed identity (Key Vault Secrets User and AcrPull). First apply might result in an unhealthy app until secrets and container image exist; that is expected for initial provisioning. An initial `AcrPull` role assignment can take time to propagate.

## 3. Container and frontend publishing

```bash
ACR=$(terraform output -raw container_registry)
az acr login --name "${ACR%%.*}"
docker build -f ../../docker/backend.Dockerfile -t "$ACR/githubmigrator-api:latest" ../..
docker push "$ACR/githubmigrator-api:latest"
```

Build React using `VITE_API_URL=https://<app-host> npm run build` from `src/frontend`; upload `dist/` to Azure Static Web Apps with its deployment token or dedicated CI workflow. Infrastructure creates the Static Web App but deliberately does not configure a GitHub OAuth token or a deployment pipeline. Web App CORS allows the Static Web App hostname. Using SWA native `/api` integration with App Service requires a later explicit linking step; Standard SKU is selected.

After image and secrets are available, restart the App Service. API bootstraps schema with `EnsureCreated` (phase-0 only); implement EF migrations before production.

## 4. AI deployment

Set `enable_ai_model = true` only after confirming `ai_model_name = "gpt-6.1-sol"`, `ai_model_version`, `ai_model_sku`, `ai_model_capacity` and regional quota. Foundry resource and project exist without the optional model. The migration engine does not call AI yet.

## 5. Security follow-up

Private networking, narrowed firewall, WAF/edge protection, database least privilege, Key Vault rotation, identity-backed Blob access, encrypted remote state, migration job workers and audit retention are required before production.
