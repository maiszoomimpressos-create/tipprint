// Migracao 2: liga cada "sistema" (chave de API) a um usuario dono (Supabase Auth),
// e trava por linha (RLS) - cada usuario so ve/mexe nos proprios sistemas.
// Contexto: o login no TipPrint Desktop nao e' do atendente do caixa - e' do CLIENTE
// que contrata a API do TipPrint pra usar no site/produto dele (ex: dono do Tipo7).
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
alter table tipprint_systems
  add column if not exists owner_id uuid references auth.users(id) on delete set null;

alter table tipprint_systems enable row level security;

drop policy if exists "owner_select" on tipprint_systems;
create policy "owner_select" on tipprint_systems
  for select using (owner_id = auth.uid());

drop policy if exists "owner_insert" on tipprint_systems;
create policy "owner_insert" on tipprint_systems
  for insert with check (owner_id = auth.uid());

drop policy if exists "owner_update" on tipprint_systems;
create policy "owner_update" on tipprint_systems
  for update using (owner_id = auth.uid());
`;

try {
  await client.connect();
  await client.query(sql);
  console.log('OK - owner_id + RLS prontos em tipprint_systems.');
} catch (e) {
  console.error('Falha na migracao:', e.message);
  process.exitCode = 1;
} finally {
  await client.end();
}
