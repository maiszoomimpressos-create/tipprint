// Migracao 3: tabela profiles + trigger que preenche ela sozinha toda vez que nasce
// um usuario novo em auth.users - nao importa de onde a conta foi criada (Desktop,
// site, admin), profiles sempre fica sincronizada.
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
create table if not exists profiles (
  id uuid primary key references auth.users(id) on delete cascade,
  email text,
  name text,
  created_at timestamptz not null default now()
);

alter table profiles enable row level security;

drop policy if exists "profiles_select_own" on profiles;
create policy "profiles_select_own" on profiles
  for select using (id = auth.uid());

drop policy if exists "profiles_update_own" on profiles;
create policy "profiles_update_own" on profiles
  for update using (id = auth.uid());

-- Gatilho: toda vez que um usuario novo nasce em auth.users, cria a linha em profiles.
create or replace function handle_new_user()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
  insert into public.profiles (id, email, name)
  values (new.id, new.email, coalesce(new.raw_user_meta_data->>'name', ''));
  return new;
end;
$$;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created
  after insert on auth.users
  for each row execute function handle_new_user();
`;

try {
  await client.connect();
  await client.query(sql);
  console.log('OK - profiles + trigger prontos.');

  // Preenche profiles pra quem ja existia em auth.users antes desse trigger existir
  // (ex: a conta de teste que criei mais cedo, se ainda existir).
  const backfill = await client.query(`
    insert into public.profiles (id, email, name)
    select u.id, u.email, ''
    from auth.users u
    left join public.profiles p on p.id = u.id
    where p.id is null
  `);
  console.log('backfill:', backfill.rowCount, 'perfil(is) preenchido(s) retroativamente.');
} catch (e) {
  console.error('Falha na migracao:', e.message);
  process.exitCode = 1;
} finally {
  await client.end();
}
