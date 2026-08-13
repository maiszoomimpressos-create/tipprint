#!/usr/bin/env node
// TipPrint · Gerador de tokens
// Fonte única: design/tipprint-tokens.json
// Saídas: web/tokens.css (CSS custom properties) e
//         windows/TipPrint.Desktop/Themes/Tokens.xaml (WPF ResourceDictionary)
// Uso: node design/tools/generate.mjs

import { readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const tokens = JSON.parse(readFileSync(join(root, "design", "tipprint-tokens.json"), "utf8"));

const kebab = (s) => s.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();

// ---------- CSS ----------
const css = [`/* Auto-generated por design/tools/generate.mjs — NÃO editar à mão.
   Fonte única: design/tipprint-tokens.json */`,
  `:root{`];
for (const [name, hex] of Object.entries(tokens.color.light)) css.push(`--tipprint-${name}:${hex};`);
css.push(`}`);
css.push(`[data-theme="raw-terminal"]{`);
for (const [name, hex] of Object.entries(tokens.color.dark)) css.push(`--tipprint-${name}:${hex};`);
css.push(`}`);
css.push(`[data-theme="raw-ink"]{`);
for (const [name, hex] of Object.entries(tokens.color.brand)) css.push(`--tipprint-brand-${name}:${hex};`);
css.push(`}`);

// Tipografia
const fams = tokens.typography.families;
css.push(`:root{`);
css.push(`--tipprint-font-ui:'${fams.ui.web}',system-ui,sans-serif;`);
css.push(`--tipprint-font-mono:'${fams.mono.web}',monospace;`);
for (const [name, size] of Object.entries(tokens.typography.scale)) {
  css.push(`--tipprint-font-size-${kebab(name)}:${size}px;`);
}
for (const [name, w] of Object.entries(tokens.typography.weights)) {
  css.push(`--tipprint-font-weight-${kebab(name)}:${w};`);
}
css.push(`}`);

// Espaço / raio / borda / motion / controles / ícones
const flat = (obj, prefix) =>
  Object.entries(obj).map(([name, v]) => `--tipprint-${prefix}-${kebab(name)}:${typeof v === "number" ? v + "px" : v};`);
css.push(`:root{`);
css.push(...flat(tokens.spacing, "space"));
css.push(...flat(tokens.radius, "radius"));
css.push(`--tipprint-stroke-hairline:${tokens.border.hairline}px;`);
for (const [name, ms] of Object.entries(tokens.motion)) {
  if (name !== "easing") css.push(`--tipprint-motion-${kebab(name)}:${ms}ms;`);
}
css.push(`--tipprint-control-min-height:${tokens.controls.minHeight}px;`);
css.push(`}`);

mkdirSync(join(root, "web"), { recursive: true });
writeFileSync(join(root, "web", "tokens.css"), css.join("\n"));
const xaml = [`<!-- Auto-generated por design/tools/generate.mjs — NÃO editar à mão.
     Fonte única: design/tipprint-tokens.json
     Uso: mesclar em App.xaml do TipPrint.Desktop e reparar referências com {StaticResource TipPrint.*} -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">`];

let xamlToken = "light";
for (const [name, hex] of Object.entries(tokens.color.light)) {
  xaml.push(`  <!-- ${name} (${xamlToken}) -->`);
  xaml.push(`  <Color x:Key="TipPrint.${name}.Color">${hex}</Color>`);
  xaml.push(`  <SolidColorBrush x:Key="TipPrint.${name}.Brush" Color="{StaticResource TipPrint.${name}.Color}" />`);
}
xamlToken = "brand";
for (const [name, hex] of Object.entries(tokens.color.brand)) {
  xaml.push(`  <!-- ${name} (${xamlToken}) -->`);
  xaml.push(`  <Color x:Key="TipPrint.Brand${name[0].toUpperCase() + name.slice(1)}.Color">${hex}</Color>`);
  xaml.push(`  <SolidColorBrush x:Key="TipPrint.Brand${name[0].toUpperCase() + name.slice(1)}.Brush" Color="{StaticResource TipPrint.Brand${name[0].toUpperCase() + name.slice(1)}.Color}" />`);
}
xaml.push(`</ResourceDictionary>`);

const winPath = join(root, "windows", "TipPrint.Desktop", "Themes");
mkdirSync(winPath, { recursive: true });
writeFileSync(join(winPath, "Tokens.xaml"), xaml.join("\n"));

// Ponto de extensão: transformar ContentPage Android para Compose futuro / JSON por plataforma
console.log("OK tokens gerados:");
console.log(" - web/tokens.css");
console.log(` - windows/TipPrint.Desktop/Themes/Tokens.xaml (${Object.keys(tokens.color.light).length + Object.keys(tokens.color.brand).length} brushes)`);