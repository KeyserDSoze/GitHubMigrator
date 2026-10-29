variable "subscription_id" {
  type        = string
  description = "Azure subscription ID."
}

variable "location" {
  type        = string
  default     = "westeurope"
  description = "Region for data plane and Foundry (model availability must be verified)."
}

variable "environment" {
  type    = string
  default = "dev"
}

variable "name_prefix" {
  type    = string
  default = "ghm"
  validation {
    condition     = can(regex("^[a-z0-9]{2,8}$", var.name_prefix))
    error_message = "name_prefix must be 2-8 lowercase alphanumeric characters."
  }
}

variable "postgres_admin_user" {
  type    = string
  default = "ghmadmin"
}

variable "postgres_admin_password" {
  type        = string
  sensitive   = true
  description = "Set using TF_VAR_postgres_admin_password, never commit tfvars."
}

variable "allowed_client_ip" {
  type        = string
  default     = null
  nullable    = true
  description = "Optional public IP to additionally allow to PostgreSQL."
}

variable "api_image_name" {
  type    = string
  default = "githubmigrator-api"
}

variable "api_image_tag" {
  type    = string
  default = "latest"
}

variable "enable_ai_model" {
  type        = bool
  default     = false
  description = "Opt in only when model region, quota, SKU and version are confirmed."
}

variable "ai_model_name" {
  type    = string
  default = "gpt-6.1-sol"
}

variable "ai_model_version" {
  type        = string
  default     = ""
  description = "Model version available in your target Azure region; required when enabled."
}

variable "ai_model_sku" {
  type    = string
  default = "GlobalStandard"
}

variable "ai_model_capacity" {
  type    = number
  default = 1
}

variable "tags" {
  type = map(string)
  default = {
    managed_by = "terraform"
    project    = "githubmigrator"
  }
}
