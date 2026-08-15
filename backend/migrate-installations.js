// Migracao: tipprint_installations - token POR INSTALACAO/MAQUINA, separado da chave de
// sistema (tipprint_systems). Existe pro fluxo de provisionamento automatico (Bluetooth,
// PC Windows): o Tipo7 pede um token novo pra cada download (POST /provision), a gente
// gera e guarda aqui, monta o ZIP com ele dentro, e o cliente final baixa via link de uso
// unico (GET /download/:token). Revogar UMA maquina nao derruba as outras nem a chave do
// sistema inteiro. Ver docs/PROVISIONAMENTO-AUTOMATICO-TIPO7.md.
import 'dotenv/config';
import pg from 'pg';

if (!process.env.DATABASE_URL) {
  console.error('Faltando DATABASE_URL no .env - encerrando.');
  process.exit(1);
}

const client = new pg.Client({
  connectionString: process.env.DATABASE_URL,
  ssl: { rejectUnauthorized: false }
});

const sql = `
create table if not exists tipprint_installations (
  id uuid primary key default gen_random_uuid(),
  system_id uuid not null references tipprint_systems(id) on delete cascade,
  token text not null unique,
  label text,
  status text not null default 'active' check (status in ('active','revoked')),
  download_token text unique,
  download_expires_at timestamptz,
  downloaded_at timestamptz,
  created_at timestamptz not null default now(),
  revoked_at timestamptz
);

create index if not exists tipprint_installations_system_id_idx on tipprint_installations(system_id);
`;

try {
  await client.connect();
  await client.query(sql);
  console.log('OK - tabela tipprint_installations pronta.');
} catch (e) {
  console.error('Falha na migracao:', e.message);
  process.exitCode = 1;
} finally {
  await client.end();
}
