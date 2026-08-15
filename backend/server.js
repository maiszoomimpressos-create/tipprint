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
import JSZip from 'jszip';
import { createClient } from '@supabase/supabase-js';

const PORT = process.env.PORT || 8090;
const ADMIN_TOKEN = process.env.ADMIN_TOKEN;
// URL publica deste backend - vai DENTRO do config.txt de cada instalacao provisionada,
// pra o PrintServer saber onde validar a propria chave (SystemCheckLoop). Hoje so' faz
// sentido pra testar no mesmo PC (localhost) - ver docs/PROVISIONAMENTO-AUTOMATICO-TIPO7.md,
// isso so' vale pra maquina remota de verdade depois que este backend for publicado.
const PUBLIC_BACKEND_URL = process.env.PUBLIC_BACKEND_URL || `http://localhost:${PORT}`;
// ZIP base ja publicado (PrintServer.exe + Instalar.bat + Desinstalar.bat) - o /download
// busca esse arquivo e so' troca o config.txt de dentro, em vez de guardar copia propria
// dos binarios. Fica sempre sincronizado com o que ja esta publicado pro site, e funciona
// mesmo se este backend for implantado isolado (sem acesso a pasta dist/ do repo).
const BASE_ZIP_URL = process.env.BASE_ZIP_URL || 'https://tipprint.vercel.app/downloads/TipPrintPrintServer.zip';

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

  // Chave de sistema direta (ex: tp_live_... usado manualmente por quem configurou o
  // PrintServer na mao) OU token de instalacao (tp_inst_..., gerado pelo /provision) -
  // os dois passam por aqui, o PrintServer/Android nao precisam saber a diferenca.
  const { data: sys, error: sysErr } = await supabase
    .from('tipprint_systems')
    .select('name, status, allowed_origins')
    .eq('api_key', key)
    .maybeSingle();
  if (sysErr) return res.status(500).json({ ok: false, error: sysErr.message });
  if (sys) {
    if (sys.status !== 'active') return res.json({ ok: true, valid: false });
    return res.json({ ok: true, valid: true, system: sys.name, allowed_origins: sys.allowed_origins });
  }

  const { data: inst, error: instErr } = await supabase
    .from('tipprint_installations')
    .select('status, tipprint_systems(name, status, allowed_origins)')
    .eq('token', key)
    .maybeSingle();
  if (instErr) return res.status(500).json({ ok: false, error: instErr.message });
  const sysOfInst = inst && inst.tipprint_systems;
  if (!inst || inst.status !== 'active' || !sysOfInst || sysOfInst.status !== 'active') {
    return res.json({ ok: true, valid: false });
  }
  res.json({ ok: true, valid: true, system: sysOfInst.name, allowed_origins: sysOfInst.allowed_origins });
});

// ---- Autenticacao por chave de SISTEMA (ex: tipo7) - usado pelo /provision. Diferente do
// requireAdmin (esse aqui e' pro proprio produto se autenticar, nao um admin humano). ----
async function requireSystemKey(req, res, next) {
  const auth = req.get('Authorization') || '';
  const key = auth.startsWith('Bearer ') ? auth.slice(7).trim() : null;
  if (!key) return res.status(401).json({ ok: false, error: 'Informe a chave do sistema em "Authorization: Bearer tp_live_...".' });
  const { data, error } = await supabase
    .from('tipprint_systems')
    .select('id, name, status')
    .eq('api_key', key)
    .maybeSingle();
  if (error) return res.status(500).json({ ok: false, error: error.message });
  if (!data || data.status !== 'active') {
    return res.status(403).json({ ok: false, error: 'Chave de sistema invalida ou revogada.' });
  }
  req.system = data;
  next();
}

function genToken(prefix) {
  const raw = [...crypto.getRandomValues(new Uint8Array(24))].map((b) => b.toString(16).padStart(2, '0')).join('');
  return prefix + raw;
}

// ---- Provisionamento automatico (ver docs/PROVISIONAMENTO-AUTOMATICO-TIPO7.md) ----
// O sistema (Tipo7) pede um token NOVO, so' pra uma instalacao/maquina - nao usa a chave
// do sistema direto no PC do cliente final. Devolve uma URL de download de uso unico que
// ja monta o ZIP com esse token dentro (config.txt pre-preenchido).

app.post('/provision', requireSystemKey, async (req, res) => {
  const label = typeof (req.body && req.body.label) === 'string' ? req.body.label.slice(0, 200) : null;
  const token = genToken('tp_inst_');
  const downloadToken = genToken('dl_');
  const expiresAt = new Date(Date.now() + 30 * 60 * 1000); // 30 min pra baixar

  const { data, error } = await supabase
    .from('tipprint_installations')
    .insert({
      system_id: req.system.id,
      token,
      label,
      download_token: downloadToken,
      download_expires_at: expiresAt.toISOString()
    })
    .select('id')
    .single();
  if (error) return res.status(500).json({ ok: false, error: error.message });

  res.json({
    ok: true,
    installationId: data.id,
    downloadUrl: `${PUBLIC_BACKEND_URL}/download/${downloadToken}`,
    expiresAt: expiresAt.toISOString()
  });
});

// GET /download/:token - uso unico. Monta o ZIP na hora (nao guarda nada em disco) a
// partir dos arquivos ja prontos em dist/, so' trocando o config.txt.
app.get('/download/:token', async (req, res) => {
  const { data, error } = await supabase
    .from('tipprint_installations')
    .select('id, token, status, download_expires_at, downloaded_at')
    .eq('download_token', req.params.token)
    .maybeSingle();
  if (error) return res.status(500).json({ ok: false, error: error.message });
  if (!data || data.status !== 'active') {
    return res.status(404).json({ ok: false, error: 'Link de download invalido ou revogado.' });
  }
  if (data.downloaded_at) {
    return res.status(410).json({ ok: false, error: 'Este link ja foi usado (uso unico). Peca um novo /provision.' });
  }
  if (new Date(data.download_expires_at) < new Date()) {
    return res.status(410).json({ ok: false, error: 'Link expirado. Peca um novo /provision.' });
  }

  let baseZipResp;
  try {
    baseZipResp = await fetch(BASE_ZIP_URL);
  } catch (e) {
    return res.status(502).json({ ok: false, error: 'Falha ao buscar o pacote base: ' + e.message });
  }
  if (!baseZipResp.ok) {
    return res.status(502).json({ ok: false, error: 'Pacote base indisponivel (HTTP ' + baseZipResp.status + ').' });
  }

  // Marca como usado ANTES de comecar a transmitir - se o download cair no meio, o cliente
  // pede um /provision novo em vez de reusar um token que talvez ja tenha chegado inteiro
  // em outra tentativa (mais seguro pender pra "usado" do que deixar reusavel indefinido).
  await supabase.from('tipprint_installations').update({ downloaded_at: new Date().toISOString() }).eq('id', data.id);

  const configTxt = ['', 'ascii', '', data.token, PUBLIC_BACKEND_URL, ''].join('\r\n');

  const zip = await JSZip.loadAsync(await baseZipResp.arrayBuffer());
  zip.file('config.txt', configTxt);
  const out = await zip.generateAsync({ type: 'nodebuffer', compression: 'DEFLATE' });

  res.setHeader('Content-Type', 'application/zip');
  res.setHeader('Content-Disposition', 'attachment; filename="TipPrintPrintServer.zip"');
  res.send(out);
});

// process.env.VERCEL so' existe rodando como Vercel Function - la' quem sobe o servidor
// HTTP e' a plataforma, nao este processo (senao da' porta ja em uso / nunca fica pronto).
// Local (npm start) continua igual, sobe na porta normal.
if (!process.env.VERCEL) {
  app.listen(PORT, () => {
    console.log(`TipPrint Backend no ar em http://localhost:${PORT}`);
  });
}

export default app;
