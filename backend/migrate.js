// Migracao unica: cria a tabela tipprint_systems (chave por sistema/produto).
// Rodar com: npm run migrate (usa DATABASE_URL do .env - conexao direta Postgres,
// so pra DDL; o server.js em si usa o client do Supabase, nao esse pg direto).
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
create table if not exists tipprint_systems (
  id uuid primary key default gen_random_uuid(),
  name text not null,
  api_key text not null unique,
  allowed_origins text[],
  status text not null default 'active' check (status in ('active','revoked')),
  created_at timestamptz not null default now(),
  revoked_at timestamptz
);

create index if not exists idx_tipprint_systems_api_key on tipprint_systems(api_key);
`;

try {
  await client.connect();
  await client.query(sql);
  console.log('OK - tabela tipprint_systems pronta.');
} catch (e) {
  console.error('Falha na migracao:', e.message);
  process.exitCode = 1;
} finally {
  await client.end();
}
