// Entrypoint da Vercel Function - so' reexporta o app Express de verdade (server.js).
// Toda rota cai aqui via o rewrite em vercel.json (source "/(.*)").
export { default } from '../server.js';
