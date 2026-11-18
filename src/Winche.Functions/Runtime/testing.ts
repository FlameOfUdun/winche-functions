/**
 * @winche/functions/testing — run a function's code in tests (Deno test, or any runner) without a server.
 *
 *   import { withSecrets, withResources, withServer } from "@winche/functions/testing";
 *
 *   Deno.test("sends", () => withSecrets({ MAIL_KEY: "test" }, () => send.run({ to: "a@b" }, testContext())));
 *
 * Secrets, resources and @winche/admin read what the innermost `with…` gives while its callback runs (one at a time:
 * don't run tests in parallel that give different ones).
 */

import { initializeAdmin, type AdminOptions, type Call } from "./admin.ts";
import type { Caller, Context } from "./functions.ts";

interface TestRun {
  call?: Call;
  secrets?: { get(name: string): string; has(name: string): boolean };
  resources?: { read(path: string): Promise<Uint8Array | null>; readText(path: string): Promise<string | null>; list(folder?: string): Promise<string[]>; has(path: string): boolean };
}

const stack: TestRun[] = [];
(globalThis as { [k: symbol]: unknown })[Symbol.for("winche.test")] = () => (stack.length ? Object.assign({}, ...stack) : undefined);

async function within<R>(run: TestRun, fn: () => R | Promise<R>): Promise<R> {
  stack.push(run);
  try { return await fn(); } finally { stack.splice(stack.lastIndexOf(run), 1); }
}

/** Runs `fn` with these secrets' values (a secret not given reads as not set). */
export function withSecrets<R>(values: { [name: string]: string | null }, fn: () => R | Promise<R>): Promise<R> {
  return within({ secrets: {
    get: (name) => {
      const v = values[name];
      if (v == null) throw Object.assign(new Error(`The secret ${name} isn't set in this test: give it to withSecrets.`), { code: "failed-precondition" });
      return v;
    },
    has: (name) => values[name] != null,
  } }, fn);
}

const clean = (p: string) => String(p ?? "").replace(/^(\.\/)+/, "").replace(/^\/+/, "");

/**
 * Runs `fn` with resources: a folder on disk (e.g. "functions/resources"; needs --allow-read) or a map of paths to
 * contents.
 */
export function withResources<R>(source: string | { [path: string]: string | Uint8Array }, fn: () => R | Promise<R>): Promise<R> {
  if (typeof source === "string") {
    const root = source.replace(/\/+$/, "");
    const exists = (p: string) => { try { return Deno.statSync(`${root}/${clean(p)}`).isFile; } catch { return false; } };
    const walk = async (dir: string, prefix: string, out: string[]) => {
      for await (const e of Deno.readDir(dir)) {
        if (e.isDirectory) await walk(`${dir}/${e.name}`, `${prefix}${e.name}/`, out);
        else if (e.isFile) out.push(prefix + e.name);
      }
    };
    return within({ resources: {
      read: async (p) => (exists(p) ? await Deno.readFile(`${root}/${clean(p)}`) : null),
      readText: async (p) => (exists(p) ? await Deno.readTextFile(`${root}/${clean(p)}`) : null),
      list: async (folder) => {
        const all: string[] = [];
        await walk(root, "", all);
        const prefix = clean(folder ?? "").replace(/\/+$/, "");
        return all.filter((p) => prefix === "" || p.startsWith(prefix + "/")).sort();
      },
      has: exists,
    } }, fn);
  }
  const files = new Map(Object.entries(source).map(([p, v]) => [clean(p), typeof v === "string" ? new TextEncoder().encode(v) : v]));
  return within({ resources: {
    read: async (p) => files.get(clean(p)) ?? null,
    readText: async (p) => (files.has(clean(p)) ? new TextDecoder().decode(files.get(clean(p))) : null),
    list: async (folder) => {
      const prefix = clean(folder ?? "").replace(/\/+$/, "");
      return [...files.keys()].filter((p) => prefix === "" || p.startsWith(prefix + "/")).sort();
    },
    has: (p) => files.has(clean(p)),
  } }, fn);
}

/**
 * Runs `fn` with @winche/admin reaching a server (the emulator, e.g. { server: "http://localhost:9090", project: "demo",
 * token }) instead of a function's run; or with a stand-in: a function that answers its calls ({ op, args } → result).
 */
export function withServer<R>(server: AdminOptions | Call, fn: () => R | Promise<R>): Promise<R> {
  if (typeof server === "function") return within({ call: server }, fn);
  const admin = initializeAdmin(server) as unknown as { transport: Call };
  return within({ call: admin.transport }, fn);
}

/** A context for calling a function's `run` in a test: its name, an optional caller, logs to the console. */
export function testContext(options?: { name?: string; caller?: Caller | null }): Context {
  return { name: options?.name ?? "test", caller: options?.caller ?? null, log: (...a: unknown[]) => console.log(...a) };
}
