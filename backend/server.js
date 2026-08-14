// TipPrint Backend - autorizacao por sistema.
//
// Contexto (decidido em conversa com o usuario, 2026-08-14): o TipPrint vai atender
// varios produtos alem do Tipo7 (bot de atendimento, sistema de vendas, parquimetro
// de carros, etc). Em vez de travar por "usuario final" (isso e' problema de cada
// produto resolver), o TipPrint autoriza por SISTEMA: cada produto ganha UMA chave.
// O PrintServer (Windows) e o app Android mandam essa chave quando chamam o TipPrint;
// esse backend so responde "essa chave e' de um sistema autorizado? sim/nao".
//
// Nao mexe em nada do fluxo de producao atual (tipo7.com -> localhost:8080 -> PrintServer
// -> impressora) - isso continua igual. Esse backend e' construido em paralelo, ainda
// nao esta ligado em nenhum PrintServer/Android real.

import 'dotenv/config';
import express from 'express';
import { createClient } from '@supabase/supabase-js';

const PORT = process.env.PORT || 8090;
const ADMIN_TOKEN = process.env.ADMIN_TOKEN;

if (!process.env.SUPABASE_URL || !process.env.SUPABASE_SERVICE_ROLE_KEY) {
  console.error('Faltando SUPABASE_URL ou SUPABASE_SERVICE_ROLE_KEY no .env - encerrando.');
  process.exit(1);
}

// service_role: usado so aqui no servidor (nunca no PrintServer/Android/site) - ignora RLS
// de proposito, porque quem controla acesso a essa tabela e' esse backend, nao o Supabase.
const supabase = createClient(process.env.SUPABASE_URL, process.env.SUPABASE_SERVICE_ROLE_KEY, {
  auth: { persistSession: false }
});

const app = express();
app.use(express.json());

function requireAdmin(req, res, next) {
  const token = req.get('X-Admin-Token');
  if (!ADMIN_TOKEN || token !== ADMIN_TOKEN) {
    return res.status(401).json({ ok: false, error: 'Token de admin invalido ou ausente (header X-Admin-Token).' });
  }
  next();
}

function genKey() {
  // Prefixo identifica que e' uma chave TipPrint (facilita reconhecer em logs/config).
  const raw = [...crypto.getRandomValues(new Uint8Array(24))].map((b) => b.toString(16).padStart(2, '0')).join('');
  return 'tp_live_' + raw;
}

app.get('/health', (_req, res) => {
  res.json({ ok: true, service: 'tipprint-backend', time: new Date().toISOString() });
});

// ---- Admin: gerenciar sistemas autorizados (Tipo7, bot, etc) ----

app.post('/systems', requireAdmin, async (req, res) => {
  const { name, allowed_origins } = req.body || {};
  if (!name || typeof name !== 'string') {
    return res.status(400).json({ ok: false, error: 'Informe "name" (ex: "tipo7").' });
  }
  const api_key = genKey();
  const { data, error } = await supabase
    .from('tipprint_systems')
    .insert({ name, api_key, allowed_origins: allowed_origins || null })
    .select()
    .single();
  if (error) return res.status(500).json({ ok: false, error: error.message });
  res.json({ ok: true, system: data });
});

app.get('/systems', requireAdmin, async (_req, res) => {
  const { data, error } = await supabase
    .from('tipprint_systems')
    .select('id, name, status, allowed_origins, created_at, revoked_at')
    .order('created_at', { ascending: false });
  if (error) return res.status(500).json({ ok: false, error: error.message });
  res.json({ ok: true, systems: data });
});

app.post('/systems/:id/revoke', requireAdmin, async (req, res) => {
  const { data, error } = await supabase
    .from('tipprint_systems')
    .update({ status: 'revoked', revoked_at: new Date().toISOString() })
    .eq('id', req.params.id)
    .select()
    .single();
  if (error) return res.status(500).json({ ok: false, error: error.message });
  res.json({ ok: true, system: data });
});

// ---- Publico: o PrintServer/Android chamam isso pra saber se a chave e' valida ----

app.post('/validate', async (req, res) => {
  const { key } = req.body || {};
  if (!key) return res.status(400).json({ ok: false, error: 'Informe "key".' });
  const { data, error } = await supabase
    .from('tipprint_systems')
    .select('name, status, allowed_origins')
    .eq('api_key', key)
    .maybeSingle();
  if (error) return res.status(500).json({ ok: false, error: error.message });
  if (!data || data.status !== 'active') {
    return res.json({ ok: true, valid: false });
  }
  res.json({ ok: true, valid: true, system: data.name, allowed_origins: data.allowed_origins });
});

app.listen(PORT, () => {
  console.log(`TipPrint Backend no ar em http://localhost:${PORT}`);
});
