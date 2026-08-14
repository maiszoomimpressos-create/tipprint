// Migracao 6: catalogo de produtos (o que uma companhia pode adquirir) + liga
// tipprint_systems.product_id -> products(id). Catalogo e' global (nao tem
// owner_id) - quem edita/publica e' o admin (via ADMIN_EMAIL, checado pelo
// claim de e-mail do JWT do Supabase). Usuarios comuns so leem produtos
// 'active' e adquirem (o que cria uma linha em tipprint_systems pra eles).
import 'dotenv/config';
import pg from 'pg';

if (!process.env.DATABASE_URL) {
  console.error('Faltando DATABASE_URL no .env - encerrando.');
  process.exit(1);
}

const ADMIN_EMAIL = 'maiszoomimpressos@gmail.com';

const client = new pg.Client({
  connectionString: process.env.DATABASE_URL,
  ssl: { rejectUnauthorized: false }
});

const sql = `
create table if not exists products (
  id uuid primary key default gen_random_uuid(),
  name text not null,
  description text,
  price_label text,
  status text not null default 'draft' check (status in ('active','draft','archived')),
  created_at timestamptz not null default now()
);

alter table products enable row level security;

drop policy if exists "products_public_select_active" on products;
create policy "products_public_select_active" on products
  for select using (
    status = 'active' or (auth.jwt() ->> 'email') = '${ADMIN_EMAIL}'
  );

drop policy if exists "products_admin_insert" on products;
create policy "products_admin_insert" on products
  for insert with check ((auth.jwt() ->> 'email') = '${ADMIN_EMAIL}');

drop policy if exists "products_admin_update" on products;
create policy "products_admin_update" on products
  for update using ((auth.jwt() ->> 'email') = '${ADMIN_EMAIL}');

drop policy if exists "products_admin_delete" on products;
create policy "products_admin_delete" on products
  for delete using ((auth.jwt() ->> 'email') = '${ADMIN_EMAIL}');

alter table tipprint_systems
  add column if not exists product_id uuid references products(id) on delete set null;
`;

try {
  await client.connect();
  await client.query(sql);
  console.log('OK - tabela products pronta + tipprint_systems.product_id ligado.');
} catch (e) {
  console.error('Falha na migracao:', e.message);
  process.exitCode = 1;
} finally {
  await client.end();
}
