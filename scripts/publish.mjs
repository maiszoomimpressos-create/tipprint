// TipPrint · Publish — sobe o APK e o update.json para o Supabase Storage
// Uso: node scripts/publish.mjs [--notes "texto"]
// Requer variáveis de ambiente:
//   SUPABASE_URL   (ex.: https://xxxx.supabase.co)
//   SUPABASE_KEY   (service_role — chave de serviço, Project Settings → API)
// O bucket "tipprint" deve existir e ser público.

import { argv, env } from "node:process";
import { readFile } from "node:fs/promises";
import { join } from "node:path";

const root = join(new URL("..", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1"));
const APK = join(root, "app", "build", "outputs", "apk", "debug", "app-debug.apk");
const UPDATE = join(root, "web", "update.json");

const parseArgs = () => {
  const notesIdx = argv.indexOf("--notes");
  return { notes: notesIdx >= 0 ? argv[notesIdx + 1] ?? "" : "" };
};

const readVersion = async () => {
  const gradle = await readFile(join(root, "app", "build.gradle.kts"), "utf8");
  const code = /versionCode\s*=\s*(\d+)/.exec(gradle)?.[1];
  const name = /versionName\s*=\s*"([^"]+)"/.exec(gradle)?.[1];
  if (!code || !name) throw new Error("versionCode/versionName não encontrados no build.gradle.kts");
  return { code: Number(code), name };
};

const upload = async (url, key, path, body, contentType) => {
  const res = await fetch(`${url}/storage/v1/object/${path}`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${key}`,
      "Content-Type": contentType,
    },
    body,
  });
  if (!res.ok) throw new Error(`upload ${path} falhou: ${res.status} ${await res.text()}`);
  return res;
};

const { notes } = parseArgs();
const { code, name } = await readVersion();
const { SUPABASE_URL: url, SUPABASE_KEY: key } = env;
if (!url || !key) throw new Error("Defina SUPABASE_URL e SUPABASE_KEY (ex.: no .env)");

const bucket = "tipprint";

const apk = await readFile(APK);
await upload(url, key, `${bucket}/app-debug.apk`, apk, "application/vnd.android.package-archive");

const update = JSON.stringify(
  { appId: "br.com.tipprint", versionCode: code, versionName: name, apkPath: "/app-debug.apk", notes },
  null,
  2
);
await upload(url, key, `${bucket}/update.json`, update, "application/json; charset=utf-8");

console.log(`✓ Publicado TipPrint v${name} (código ${code}) no Supabase:`);
console.log(`  APK:    ${url}/storage/v1/object/public/${bucket}/app-debug.apk`);
console.log(`  update: ${url}/storage/v1/object/public/${bucket}/update.json`);
console.log(`  notas:  ${notes || "(sem notas)"}`);