data "azurerm_client_config" "current" {}

resource "random_string" "suffix" {
  length  = 6
  lower   = true
  upper   = false
  numeric = true
  special = false
}

locals {
  id           = "${var.name_prefix}${var.environment}${random_string.suffix.result}"
  resource_tags = merge(var.tags, { environment = var.environment })
}

resource "azurerm_resource_group" "main" {
  name     = "rg-${local.id}"
  location = var.location
  tags     = local.resource_tags
}

resource "azurerm_log_analytics_workspace" "logs" {
  name                = "log-${local.id}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.resource_tags
}

resource "azurerm_application_insights" "api" {
  name                = "appi-${local.id}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.logs.id
  tags                = local.resource_tags
}

resource "azurerm_container_registry" "api" {
  name                = "${local.id}acr"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Basic"
  admin_enabled       = false
  tags                = local.resource_tags
}

resource "azurerm_service_plan" "api" {
  name                = "asp-${local.id}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  os_type             = "Linux"
  sku_name            = "B1"
  tags                = local.resource_tags
}

resource "azurerm_storage_account" "artifacts" {
  name                            = "${local.id}sa"
  resource_group_name             = azurerm_resource_group.main.name
  location                        = azurerm_resource_group.main.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  account_kind                    = "StorageV2"
  min_tls_version                 = "TLS1_2"
  public_network_access_enabled   = true
  allow_nested_items_to_be_public = false
  tags                            = local.resource_tags
}

resource "azurerm_storage_container" "migration_artifacts" {
  name                  = "migration-artifacts"
  storage_account_id    = azurerm_storage_account.artifacts.id
  container_access_type = "private"
}

resource "azurerm_postgresql_flexible_server" "db" {
  name                          = "psql-${local.id}"
  resource_group_name           = azurerm_resource_group.main.name
  location                      = azurerm_resource_group.main.location
  version                       = "17"
  administrator_login           = var.postgres_admin_user
  administrator_password        = var.postgres_admin_password
  sku_name                      = "B_Standard_B1ms"
  storage_mb                    = 32768
  backup_retention_days         = 7
  public_network_access_enabled = true
  tags                          = local.resource_tags
}

resource "azurerm_postgresql_flexible_server_database" "main" {
  name      = "githubmigrator"
  server_id = azurerm_postgresql_flexible_server.db.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# WARNING: allows connections from Azure-hosted services beyond this App Service.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "allow-azure-services-dev-only"
  server_id        = azurerm_postgresql_flexible_server.db.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "operator" {
  count            = var.allowed_client_ip == null ? 0 : 1
  name             = "operator-allowlist"
  server_id        = azurerm_postgresql_flexible_server.db.id
  start_ip_address = var.allowed_client_ip
  end_ip_address   = var.allowed_client_ip
}

resource "azurerm_key_vault" "main" {
  name                          = "kv-${local.id}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  enable_rbac_authorization     = true
  public_network_access_enabled = true
  soft_delete_retention_days    = 7
  purge_protection_enabled      = false
  tags                          = local.resource_tags
}

resource "azurerm_static_web_app" "frontend" {
  name                          = "swa-${local.id}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  sku_tier                      = "Standard"
  sku_size                      = "Standard"
  public_network_access_enabled = true
  tags                          = local.resource_tags
}

resource "azurerm_cognitive_account" "foundry" {
  name                          = "ai-${local.id}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  kind                          = "AIServices"
  sku_name                      = "S0"
  custom_subdomain_name         = "ai-${local.id}"
  project_management_enabled    = true
  public_network_access_enabled = true
  identity {
    type = "SystemAssigned"
  }
  tags = local.resource_tags
}

resource "azurerm_cognitive_account_project" "migration" {
  name                 = "migration"
  cognitive_account_id = azurerm_cognitive_account.foundry.id
  location             = azurerm_resource_group.main.location
  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_cognitive_deployment" "pipeline_rewrite" {
  count                = var.enable_ai_model ? 1 : 0
  name                 = "pipeline-rewrite"
  cognitive_account_id = azurerm_cognitive_account.foundry.id

  model {
    format  = "OpenAI"
    name    = var.ai_model_name
    version = var.ai_model_version
  }
  sku {
    name     = var.ai_model_sku
    capacity = var.ai_model_capacity
  }

  lifecycle {
    precondition {
      condition     = length(var.ai_model_version) > 0
      error_message = "Specify ai_model_version to deploy a model."
    }
  }
}

resource "azurerm_linux_web_app" "api" {
  name                = "app-${local.id}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  service_plan_id     = azurerm_service_plan.api.id
  https_only          = true

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on                               = true
    container_registry_use_managed_identity = true
    application_stack {
      docker_registry_url = "https://${azurerm_container_registry.api.login_server}"
      docker_image_name   = "${var.api_image_name}:${var.api_image_tag}"
    }
  }

  app_settings = {
    WEBSITES_PORT                          = "8080"
    ASPNETCORE_ENVIRONMENT                = "Production"
    Observability__Provider               = "AzureMonitor"
    APPLICATIONINSIGHTS_CONNECTION_STRING = azurerm_application_insights.api.connection_string
    Frontend__Origin                      = "https://${azurerm_static_web_app.frontend.default_host_name}"
    Jwt__SigningKey                       = "@Microsoft.KeyVault(VaultName=${azurerm_key_vault.main.name};SecretName=jwt-signing-key)"
    Bootstrap__Username                   = "superadmin"
    Bootstrap__Password                   = "@Microsoft.KeyVault(VaultName=${azurerm_key_vault.main.name};SecretName=bootstrap-password)"
    ConnectionStrings__PostgreSql         = "@Microsoft.KeyVault(VaultName=${azurerm_key_vault.main.name};SecretName=postgres-connection-string)"
    Storage__ConnectionString             = "@Microsoft.KeyVault(VaultName=${azurerm_key_vault.main.name};SecretName=storage-connection-string)"
    Storage__Container                    = azurerm_storage_container.migration_artifacts.name
    Foundry__Endpoint                     = azurerm_cognitive_account.foundry.endpoint
    Foundry__Deployment                   = var.enable_ai_model ? azurerm_cognitive_deployment.pipeline_rewrite[0].name : ""
  }

  tags = local.resource_tags
}

resource "azurerm_role_assignment" "acr_pull" {
  scope                = azurerm_container_registry.api.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_linux_web_app.api.identity[0].principal_id
}

resource "azurerm_role_assignment" "key_vault_reader" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_web_app.api.identity[0].principal_id
}

resource "azurerm_role_assignment" "foundry_inference" {
  scope                = azurerm_cognitive_account.foundry.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_linux_web_app.api.identity[0].principal_id
}
