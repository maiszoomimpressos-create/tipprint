// Migracao 4: campos de cadastro completo em profiles (perfil do dashboard).
// "name" ja existia (usado como nome de exibicao no menu de conta) - agora
// vira "Nome completo" na tela de Perfil. Os demais sao novos.
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
alter table profiles add column if not exists cpf text;
alter table profiles add column if not exists rg text;
alter table profiles add column if not exists birth_date date;
alter table profiles add column if not exists address_street text;
alter table profiles add column if not exists address_number text;
alter table profiles add column if not exists address_neighborhood text;
alter table profiles add column if not exists address_city text;
alter table profiles add column if not exists address_zip text;
alter table profiles add column if not exists phone text;
`;

try {
  await client.connect();
  await client.query(sql);
  console.log('OK - campos de perfil (cpf, rg, nascimento, endereco, telefone) prontos.');
} catch (e) {
  console.error('Falha na migracao:', e.message);
  process.exitCode = 1;
} finally {
  await client.end();
}
