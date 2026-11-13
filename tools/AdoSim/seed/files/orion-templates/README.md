# orion-templates

Reusable Azure Pipelines templates for the Orion Commerce platform.

Reference them from any pipeline with a repository resource:

```yaml
resources:
  repositories:
  - repository: templates
    type: git
    name: Orion Platform/orion-templates
    ref: refs/heads/main

steps:
- template: steps/security-scan.yml@templates
```

| Folder | Content |
| --- | --- |
| `steps/` | dependency scan, Terraform plan, Teams notification |
| `jobs/` | container image build, web app deployment job |
| `stages/` | full deployment stage for one environment |
| `variables/` | shared variable definitions |

Pin production pipelines to a tag (`ref: refs/tags/v3`) instead of `main`.
