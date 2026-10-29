output "resource_group" {
  value = azurerm_resource_group.main.name
}

output "container_registry" {
  value = azurerm_container_registry.api.login_server
}

output "key_vault_name" {
  value = azurerm_key_vault.main.name
}

output "postgres_host" {
  value = azurerm_postgresql_flexible_server.db.fqdn
}

output "storage_account_name" {
  value = azurerm_storage_account.artifacts.name
}

output "api_url" {
  value = "https://${azurerm_linux_web_app.api.default_hostname}"
}

output "frontend_url" {
  value = "https://${azurerm_static_web_app.frontend.default_host_name}"
}

output "foundry_endpoint" {
  value = azurerm_cognitive_account.foundry.endpoint
}

output "foundry_deployment_name" {
  value = var.enable_ai_model ? azurerm_cognitive_deployment.pipeline_rewrite[0].name : null
}
