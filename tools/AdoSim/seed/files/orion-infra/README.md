# orion-infra

Infrastructure as code for the Orion estate.

| Folder | Purpose |
| --- | --- |
| `terraform/` | App Service plans and resource groups |
| `bicep/` | Alternative Bicep deployment and monitoring |
| `pipelines/` | `terraform.yml` and `bicep.yml` (multi-repo checkout, compile-time expressions, approvals) |

Production applies run in the `infra-prod` environment, which requires an approval.
