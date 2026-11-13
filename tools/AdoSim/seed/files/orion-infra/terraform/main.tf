terraform {
  required_version = ">= 1.9.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.110"
    }
  }

  backend "azurerm" {
    resource_group_name  = "rg-orion-tfstate"
    storage_account_name = "storionstate"
    container_name       = "tfstate"
  }
}

provider "azurerm" {
  features {}
}

variable "environment" {
  type        = string
  description = "dev, staging or prod"
}

resource "azurerm_resource_group" "main" {
  name     = "rg-orion-${var.environment}"
  location = "westeurope"
}

resource "azurerm_service_plan" "api" {
  name                = "asp-orion-${var.environment}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  os_type             = "Linux"
  sku_name            = var.environment == "prod" ? "P1v3" : "B1"
}
