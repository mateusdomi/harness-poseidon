-- RBAC multiusuário: papel fechado por perfil. Instalações existentes (modo
-- pessoal, perfil único) tornam-se admin; adesões a tenant compartilhado nascem member.
ALTER TABLE harness.local_users ADD COLUMN role text NOT NULL DEFAULT 'admin' CHECK (role IN ('admin', 'member'));
