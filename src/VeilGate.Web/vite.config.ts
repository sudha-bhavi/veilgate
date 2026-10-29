import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// The console's Content-Security-Policy, as the API sends it in production. Keep it in step with
// SecurityHeadersMiddleware.ConsolePolicy. `npm run build && npm run preview` serves the built console under it, so a
// change that the policy would block shows up as a console error before release. The dev server (npm run dev) does not
// use it: Vite's hot reload needs inline scripts and a websocket that production never uses.
const consolePolicy = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "font-src 'self'",
  "connect-src 'self'",
  "worker-src 'self'",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'self'",
  "frame-ancestors 'none'",
].join("; ");

// In development the browser talks only to Vite (same origin) and Vite forwards API calls to the .NET API.
// No CORS is involved, and the HttpOnly refresh cookie behaves as it will in production.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5080",
      "/oauth": "http://localhost:5080",
    },
  },
  preview: {
    port: 4173,
    headers: { "Content-Security-Policy": consolePolicy },
    // preview uses the same proxy as the dev server, so the built console can sign in against the local API
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test/setup.ts"],
  },
});
