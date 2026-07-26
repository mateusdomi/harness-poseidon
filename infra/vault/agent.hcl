pid_file = "/tmp/poseidon-vault-agent.pid"

vault {
  address = env("VAULT_ADDR")
}

auto_auth {
  method "approle" {
    mount_path = "auth/approle"
    config = {
      role_id_file_path = "/run/secrets/vault_role_id"
      secret_id_file_path = "/run/secrets/vault_secret_id"
      remove_secret_id_file_after_reading = false
    }
  }
}

template {
  source = "/vault/templates/database_connection_string.ctmpl"
  destination = "/vault/rendered/database_connection_string"
  perms = "0400"
}
