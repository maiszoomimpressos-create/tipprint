// TipPrint · Servidor estático de preview (sem dependências)
// Iniciar: node web/server.mjs   →  http://localhost:3005
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(fileURLToPath(new URL(".", import.meta.url)));
const port = Number(process.env.PORT || 3005);

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".svg": "image/svg+xml",
  ".json": "application/json; charset=utf-8",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".ttf": "font/ttf",
  ".woff": "font/woff",
  ".woff2": "font/woff2",
};

createServer(async (req, res) => {
  try {
    const raw = decodeURIComponent((req.url || "/").split("?")[0]);
    if (raw === "/" || raw === "") {
      return res.writeHead(302, { Location: "/index.html" }).end();
    }
    let path = normalize(raw);
    const file = join(root, path);
    if (!file.startsWith(root)) throw Object.assign(new Error("fora do root"), { code: "EACCES" });
    const body = await readFile(file);
    res.writeHead(200, {
      "Content-Type": MIME[extname(file).toLowerCase()] || "application/octet-stream",
      "Cache-Control": "no-store",
    });
    res.end(body);
  } catch (err) {
    console.error("[server.mjs]", req.url, err.message);
    const status = err.code === "ENOENT" ? 404 : err.code === "EACCES" ? 403 : 500;
    res.writeHead(status, { "Content-Type": "text/plain; charset=utf-8" });
    res.end(status + " " + (err.message || ""));
  }
}).listen(port, () => {
  console.log(`TipPrint preview → http://localhost:${port}`);
});