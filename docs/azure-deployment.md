# Azure foundation

Terraform under `infra/terraform` provisions publicly reachable services without VNet, as requested. It is a **development foundation**, NOT a production-hardening reference.

Prerequisites: Azure subscription, Terraform, Azure CLI, appropriate RBAC, supported region/model, an ACR image pushed under the configured tag, and a separate secure Terraform remote backend. Provide sensitive variables securely, e.g. via CI secret environment variables `TF_VAR_postgres_admin_password` and `TF_VAR_bootstrap_admin_password`. Never commit a live `.tfvars` or Terraform state.

```bash
cd infra/terraform
cp terraform.tfvars.example terraform.tfvars
terraform init
terraform plan
terraform apply
```

Then build and push `docker/backend.Dockerfile` to ACR; set image tag and redeploy the app. Build `src/frontend` and deploy `dist/` to Static Web Apps using a deployment token / CI (not configured by Terraform). API requests from SWA can use the App Service origin with configured CORS initially. Standard-plan SWA supports linking App Service as a managed `/api` proxy but linkage needs explicit follow-up provisioning/configuration.

Secrets are referenced via Key Vault in App Service app settings and retrieved through managed identity; Terraform-managed secret VALUES may still appear in Terraform state, even if marked sensitive: use encrypted remote state and strict access controls. ACR pull uses managed identity. PostgreSQL is public with Azure-service access rule for initial connectivity, which is broader than least privilege. Restrict firewall/public access before handling real migration credentials.

Foundry `gpt-6.1-sol` deployment is opt-in (`enable_ai_model`) until regional model version, quota and capacity have been validated. AI must never rewrite pipelines without review; later features will use explicit prompts and a human approval step.
