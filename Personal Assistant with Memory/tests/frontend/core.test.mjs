import test from "node:test";
import assert from "node:assert/strict";
import { request, ApiError, filterConversations, savedConversationId, memoryLabel } from "../../src/MemoryAssistant.Api/wwwroot/chat-core.mjs";

test("history search matches accents and case without changing titles", () => {
  const conversations = [{ title: "Organización del proyecto" }, { title: "Preferencias de C#" }];
  assert.deepEqual(filterConversations(conversations, " ORGANIZACION "), [conversations[0]]);
  assert.equal(conversations[0].title, "Organización del proyecto");
});

test("storage denial and malformed remembered ids do not break startup", () => {
  assert.equal(savedConversationId({ getItem() { throw new Error("denied"); } }), null);
  assert.equal(savedConversationId({ getItem: () => "<script>private data</script>" }), null);
  assert.equal(savedConversationId({ getItem: () => "11111111-1111-1111-1111-111111111111" }), "11111111-1111-1111-1111-111111111111");
});

test("delete and replacement proposals have different consent labels", () => {
  assert.equal(memoryLabel({ operation: "Delete" }), "Propuesta de olvido");
  assert.equal(memoryLabel({ operation: "Replace", replaces: [1, 2] }), "Propuesta de fusión");
  assert.equal(memoryLabel({ operation: "Replace", replaces: [1] }), "Propuesta de corrección");
});

test("API mutations send JSON to same origin without caching transcripts", async t => {
  t.mock.method(globalThis, "fetch", async (path, options) => {
    assert.equal(path, "/api/conversations/id/messages");
    assert.equal(options.credentials, "same-origin");
    assert.equal(options.cache, "no-store");
    assert.equal(options.headers["Content-Type"], "application/json");
    assert.deepEqual(JSON.parse(options.body), { message: "Hola" });
    return new Response(JSON.stringify({ answer: "Hola" }), { status: 200 });
  });
  assert.deepEqual(await request("/api/conversations/id/messages", { method: "POST", body: { message: "Hola" } }), { answer: "Hola" });
});

test("version conflicts preserve the server explanation for the user", async t => {
  t.mock.method(globalThis, "fetch", async () => new Response(JSON.stringify({ detail: "La versión cambió.", errorCode: "version_conflict" }), { status: 409 }));
  await assert.rejects(request("/api/memories/id", { method: "PATCH", body: {} }), error => error instanceof ApiError && error.status === 409 && error.code === "version_conflict" && error.message === "La versión cambió.");
});

test("empty successful DELETE responses are accepted", async t => {
  t.mock.method(globalThis, "fetch", async () => new Response(null, { status: 204 }));
  assert.equal(await request("/api/memories/id", { method: "DELETE" }), null);
});

test("aborting a turn never becomes an automatic retry", async t => {
  let calls = 0;
  t.mock.method(globalThis, "fetch", async () => { calls++; throw new DOMException("Aborted", "AbortError"); });
  await assert.rejects(request("/api/conversations/id/messages", { method: "POST", body: { message: "Hola" } }), { name: "AbortError" });
  assert.equal(calls, 1);
});

test("network failures and unexpected HTML return readable errors", async t => {
  const mocked = t.mock.method(globalThis, "fetch", async () => { throw new TypeError("Failed to fetch"); });
  await assert.rejects(request("/api/conversations"), error => error instanceof ApiError && error.status === 0 && error.message.includes("conectar"));
  mocked.mock.mockImplementation(async () => new Response("<html>Error</html>", { status: 502 }));
  await assert.rejects(request("/api/conversations"), error => error instanceof ApiError && error.status === 502 && !error.message.includes("<html>"));
});
