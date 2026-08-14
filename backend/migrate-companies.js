// Migracao 5: tabela companies (identificacao/certificacao das empresas donas de
// sistema, ex: Tipo7) + liga tipprint_systems.company_id -> companies(id).
// CNPJ/razao social fazem pra empresa o que CPF/RG fazem pra pessoa em profiles.
// responsible_profile_id aponta pra profiles (pessoa fisica responsavel, ja
// cadastrada em Perfil) - por padrao e' o proprio dono da conta.
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
create table if not exists companies (
  id uuid primary key default gen_random_uuid(),
  owner_id uuid references auth.users(id) on delete cascade,
  legal_name text,
  trade_name text,
  cnpj text,
  contact_email text,
  contact_phone text,
  address_street text,
  address_number text,
  address_neighborhood text,
  address_city text,
  address_zip text,
  responsible_profile_id uuid references profiles(id) on delete set null,
  status text not null default 'active' check (status in ('active','inactive')),
  created_at timestamptz not null default now()
);

alter table companies enable row level security;

drop policy if exists "companies_owner_select" on companies;
create policy "companies_owner_select" on companies
  for select using (owner_id = auth.uid());

drop policy if exists "companies_owner_insert" on companies;
create policy "companies_owner_insert" on companies
  for insert with check (owner_id = auth.uid());

drop policy if exists "companies_owner_update" on companies;
create policy "companies_owner_update" on companies
  for update using (owner_id = auth.uid());

drop policy if exists "companies_owner_delete" on companies;
create policy "companies_owner_delete" on companies
  for delete using (owner_id = auth.uid());

alter table tipprint_systems
  add column if not exists company_id uuid references companies(id) on delete set null;
`;

try {
  await client.connect();
  await client.query(sql);
  console.log('OK - tabela companies pronta + tipprint_systems.company_id ligado.');
} catch (e) {
  console.error('Falha na migracao:', e.message);
  process.exitCode = 1;
} finally {
  await client.end();
}
