import { request, filterConversations, displayTime, memoryLabel, savedConversationId } from "./chat-core.mjs";

const $ = id => document.getElementById(id);
const state = {
  activeId: null, conversation: null, conversations: [], memories: [], tab: "Active",
  busy: null, controller: null, dialog: null, failedDraft: "", drafts: new Map(), metadata: new Map(),
  historyError: false, memoryError: false, historyLoaded: false, memoryLoaded: false
};
try { state.activeId = savedConversationId(localStorage); } catch { /* Storage can be disabled. */ }
let toastTimer;

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function icon(name) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.classList.add("icon");
  svg.setAttribute("aria-hidden", "true");
  const use = document.createElementNS(svg.namespaceURI, "use");
  use.setAttribute("href", `#i-${name}`);
  svg.append(use);
  return svg;
}

function button(text, className, name, action) {
  const node = element("button", className);
  node.type = "button";
  if (name) node.append(icon(name));
  if (text) node.append(document.createTextNode(text));
  node.addEventListener("click", action);
  return node;
}

function persistSelection() {
  try {
    if (state.activeId) localStorage.setItem("memoria.activeConversation", state.activeId);
    else localStorage.removeItem("memoria.activeConversation");
  } catch { /* Server-side history still works when browser storage is unavailable. */ }
}

function showNotice(message, restore = false) {
  $("notice-text").textContent = message;
  $("notice").hidden = false;
  $("restore-draft").hidden = !restore || !state.failedDraft;
}

function toast(message) {
  clearTimeout(toastTimer);
  $("toast").textContent = message;
  $("toast").hidden = false;
  toastTimer = setTimeout(() => { $("toast").hidden = true; }, 4500);
}

function updateComposer() {
  const input = $("message-input");
  input.style.height = "auto";
  input.style.height = `${Math.min(input.scrollHeight, 160)}px`;
  $("send-message").disabled = !!state.busy || !input.value.trim();
  $("send-message").hidden = state.busy === "sending";
  $("stop-send").hidden = state.busy !== "sending";
  $("character-count").hidden = input.value.length < 3200;
  $("character-count").textContent = `${input.value.length} / 4000`;
}

function setBusy(value) {
  state.busy = value;
  $("new-chat").disabled = !!value;
  $("delete-chat").disabled = !!value;
  $("refresh-history").disabled = !!value;
  $("refresh-memories").disabled = !!value;
  $("messages").setAttribute("aria-busy", String(value === "sending" || value === "loading"));
  $("thinking").hidden = value !== "sending";
  for (const node of document.querySelectorAll(".conversation-item,.memory-card-actions button,.suggestion")) node.disabled = !!value;
  updateComposer();
}

function saveDraft() { state.drafts.set(state.activeId || "new", $("message-input").value); }
function restoreDraftForConversation() { $("message-input").value = state.drafts.get(state.activeId || "new") || ""; updateComposer(); }

function setMemoryOpen(open) {
  $("memory-panel").hidden = !open;
  $("memory-toggle").setAttribute("aria-expanded", String(open));
  document.body.classList.toggle("memory-closed", !open);
  if (open && window.innerWidth < 1280) setNavOpen(false);
  updateScrim();
  if (open && window.innerWidth < 1280) $("close-memory").focus();
}

function setNavOpen(open) {
  document.body.classList.toggle("nav-open", open);
  $("menu-toggle").setAttribute("aria-expanded", String(open));
  if (open && window.innerWidth < 1280) {
    $("memory-panel").hidden = true;
    document.body.classList.add("memory-closed");
    $("memory-toggle").setAttribute("aria-expanded", "false");
  }
  $("sidebar").inert = window.innerWidth <= 900 && !open;
  updateScrim();
  if (open) $("history-search").focus();
}

function updateScrim() {
  const navOpen = window.innerWidth <= 900 && document.body.classList.contains("nav-open");
  const memoryOpen = window.innerWidth < 1280 && !$("memory-panel").hidden;
  $("scrim").hidden = !navOpen && !memoryOpen;
  document.querySelector("main").inert = navOpen || memoryOpen;
  $("sidebar").inert = memoryOpen || window.innerWidth <= 900 && !navOpen;
  $("memory-panel").inert = $("memory-panel").hidden;
}

function renderHeader() {
  const conversation = state.conversations.find(c => c.id === state.activeId);
  $("chat-title").textContent = conversation?.title || "Nueva conversación";
  $("delete-chat").hidden = !state.activeId;
  document.title = state.activeId && conversation ? `${conversation.title} · Memoria` : "Memoria · Tu asistente personal";
}

function renderConversations() {
  const list = $("conversation-list");
  list.replaceChildren();
  if (state.historyError) {
    list.append(element("p", "list-empty", "No se pudo cargar el historial. Usa el botón de actualizar para volver a intentarlo."));
  } else {
    const conversations = filterConversations(state.conversations, $("history-search").value);
    if (!conversations.length) list.append(element("p", "list-empty", $("history-search").value ? "No hay conversaciones que coincidan." : "Tu próxima conversación empieza aquí."));
    for (const conversation of conversations) {
      const node = button("", "conversation-item", "chat", () => openConversation(conversation.id));
      node.append(element("span", "", conversation.title));
      node.title = conversation.title;
      node.disabled = !!state.busy;
      if (conversation.id === state.activeId) { node.classList.add("current"); node.setAttribute("aria-current", "page"); }
      list.append(node);
    }
  }
  renderHeader();
}

// Model and user text are always inserted as text nodes, including Markdown code blocks.
function appendInline(parent, text) {
  const fragments = text.split(/(\*\*[^*\n]+\*\*|`[^`\n]+`)/g);
  for (const fragment of fragments) {
    if (fragment.startsWith("**") && fragment.endsWith("**")) parent.append(element("strong", "", fragment.slice(2, -2)));
    else if (fragment.startsWith("`") && fragment.endsWith("`")) parent.append(element("code", "", fragment.slice(1, -1)));
    else parent.append(document.createTextNode(fragment));
  }
}

function appendRichText(parent, text) {
  const sections = text.split(/```[^\n`]*\n([\s\S]*?)(?:```|$)/g);
  sections.forEach((part, index) => {
    if (!part) return;
    if (index % 2) { const pre = element("pre"); pre.append(element("code", "", part)); parent.append(pre); }
    else {
      for (const paragraph of part.split(/\n\s*\n/)) {
        const node = element("p");
        appendInline(node, paragraph);
        parent.append(node);
      }
    }
  });
}

function renderMessages(scroll = false) {
  const list = $("messages");
  list.replaceChildren();
  const messages = state.conversation?.messages || [];
  $("welcome").hidden = messages.length > 0 || state.busy === "sending";
  for (const message of messages) {
    if (message.excluded) {
      list.append(element("div", "message excluded", "Un mensaje se ha excluido por olvido o corrección."));
      continue;
    }
    const row = element("article", `message ${message.role === "user" ? "user" : "assistant"}`);
    if (message.role !== "user") { const avatar = element("span", "assistant-avatar"); avatar.append(icon("memory")); row.append(avatar); }
    const body = element("div", "message-body");
    const label = element("div", "message-label", message.role === "user" ? "Tú" : "Memoria");
    const time = element("time", "", displayTime(message.createdAt));
    if (message.createdAt) time.dateTime = message.createdAt;
    label.append(time);
    if (message.role !== "user") {
      const copy = button("", "icon-button", "copy", async () => {
        try { await navigator.clipboard.writeText(message.content); toast("Respuesta copiada."); }
        catch { toast("No se pudo copiar. Puedes seleccionar el texto de la respuesta."); }
      });
      copy.setAttribute("aria-label", "Copiar respuesta");
      copy.title = "Copiar respuesta";
      label.append(copy);
    }
    body.append(label);
    const content = element("div", "message-content");
    if (message.role === "user") content.textContent = message.content;
    else appendRichText(content, message.content);
    body.append(content);
    const metadata = state.metadata.get(message.id);
    if (metadata?.retrievedMemories?.length) {
      const details = element("details", "message-metadata");
      details.append(element("summary", "", metadata.retrievedMemories.length === 1 ? "Se ha consultado 1 recuerdo" : `Se han consultado ${metadata.retrievedMemories.length} recuerdos`));
      const items = element("ul");
      for (const hit of metadata.retrievedMemories) items.append(element("li", "", hit.content));
      details.append(items);
      body.append(details);
    }
    row.append(body);
    list.append(row);
  }
  if (scroll) requestAnimationFrame(() => { $("chat-scroll").scrollTop = $("chat-scroll").scrollHeight; });
}

async function refreshConversations() {
  try { state.conversations = await request("/api/conversations"); state.historyError = false; state.historyLoaded = true; }
  catch (error) { state.historyError = true; renderConversations(); throw error; }
  renderConversations();
}

async function refreshMemories() {
  try { state.memories = await request("/api/memories"); state.memoryError = false; state.memoryLoaded = true; }
  catch (error) { state.memoryError = true; renderMemories(); throw error; }
  renderMemories();
}

async function refreshCurrentConversation() {
  if (!state.activeId) return;
  try { state.conversation = await request(`/api/conversations/${state.activeId}`); }
  catch (error) {
    if (error.status === 404) { state.activeId = null; state.conversation = null; persistSelection(); renderHeader(); }
    throw error;
  }
  renderMessages();
}

async function refreshAfterMutation({ scrub = false } = {}) {
  if (scrub) {
    state.memories = []; state.conversations = []; state.conversation = null; state.metadata.clear();
    $("chat-announcement").textContent = "";
    renderMessages(); renderConversations(); renderMemories();
  }
  const results = await Promise.allSettled([refreshConversations(), refreshMemories(), refreshCurrentConversation()]);
  const errors = results.filter(r => r.status === "rejected");
  if (errors.length) showNotice(`El cambio se guardó, pero no se pudo actualizar toda la vista. ${errors[0].reason.message}`);
}

async function checkConnection() {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 7000);
  try { await request("/health/ready", { signal: controller.signal }); setConnection(true); }
  catch { setConnection(false); }
  finally { clearTimeout(timer); }
}

function setConnection(online) {
  $("connection-status").classList.toggle("offline", !online);
  $("connection-status").querySelector("span").textContent = online ? "Espacio disponible" : "Sin conexión a datos";
}

async function openConversation(id) {
  if (state.busy || id === state.activeId && state.conversation) return;
  saveDraft();
  setBusy("loading");
  setNavOpen(false);
  try {
    const conversation = await request(`/api/conversations/${id}`);
    state.activeId = id; state.conversation = conversation; persistSelection();
    $("chat-announcement").textContent = "";
    restoreDraftForConversation(); renderConversations(); renderMessages(true);
    $("notice").hidden = true;
  } catch (error) {
    if (error.status === 404) await refreshConversations().catch(() => {});
    showNotice(error.message);
  } finally { setBusy(null); }
}

function newConversation() {
  if (state.busy) return;
  saveDraft();
  state.activeId = null; state.conversation = null; state.metadata.clear(); state.failedDraft = "";
  $("chat-announcement").textContent = "";
  persistSelection(); restoreDraftForConversation(); renderConversations(); renderMessages();
  $("notice").hidden = true; setNavOpen(false);
  if (window.innerWidth < 1280) setMemoryOpen(false);
  $("message-input").focus();
}

async function sendMessage(event) {
  event.preventDefault();
  const text = $("message-input").value.trim();
  if (state.busy || !text || text.length > 4000) return;
  setBusy("sending");
  $("notice").hidden = true;
  state.failedDraft = "";
  const draftKey = state.activeId || "new";
  state.drafts.delete(draftKey);
  $("message-input").value = ""; updateComposer();
  const controller = new AbortController();
  state.controller = controller;
  try {
    if (!state.activeId) {
      state.conversation = await request("/api/conversations", { method: "POST", signal: controller.signal });
      state.activeId = state.conversation.id;
      persistSelection();
    }
    if (!state.conversation) state.conversation = await request(`/api/conversations/${state.activeId}`, { signal: controller.signal });
    const optimistic = { id: `draft-${Date.now()}`, role: "user", content: text, excluded: false, createdAt: new Date().toISOString() };
    state.conversation.messages.push(optimistic);
    renderMessages(true); renderHeader();
    const response = await request(`/api/conversations/${state.activeId}/messages`, { method: "POST", body: { message: text }, signal: controller.signal });
    const provisional = { id: `answer-${Date.now()}`, role: "assistant", content: response.answer, excluded: false, createdAt: new Date().toISOString() };
    state.conversation.messages.push(provisional);
    state.metadata.set(provisional.id, response);
    $("chat-announcement").textContent = response.answer.length > 600 ? `${response.answer.slice(0, 600)}. Respuesta completa en la conversación.` : response.answer;
    renderMessages(true);
    // A successful answer must remain visible even if refreshing its persisted transcript fails.
    try {
      await refreshCurrentConversation();
      const last = state.conversation?.messages.findLast(m => m.role === "assistant" && !m.excluded);
      if (last) state.metadata.set(last.id, response);
    } catch (error) { showNotice(`La respuesta se recibió, pero no se pudo actualizar el historial. ${error.message}`); }
    const maintenance = await Promise.allSettled([refreshConversations(), refreshMemories()]);
    if (maintenance.some(r => r.status === "rejected")) showNotice("La respuesta se recibió. No se pudo actualizar el historial o los recuerdos; puedes volver a cargarlos.");
    if (response.warnings?.length) showNotice(response.warnings.join(" "));
    if (response.candidates?.length) {
      selectMemoryTab("Pending");
      toast(response.candidates.length === 1 ? "Hay un nuevo recuerdo por revisar." : "Hay nuevos recuerdos por revisar.");
    }
    renderMessages(true); setConnection(true);
  } catch (error) {
    state.failedDraft = text;
    setBusy("loading");
    const explanation = error.name === "AbortError" ? "Solicitud detenida. Revisa el historial antes de volver a enviar el mensaje." : error.message;
    showNotice(explanation, true);
    // The server may already have committed the user message or a tool proposal. Never retry automatically.
    const current = await Promise.allSettled([refreshCurrentConversation(), refreshConversations(), refreshMemories()]);
    if (current[0].status === "rejected") { state.conversation = null; renderMessages(); }
    showNotice(explanation, true);
    if (error.status === 503 || error.status === 0) setConnection(false);
  } finally {
    state.controller = null; setBusy(null); renderMessages();
    if (window.innerWidth > 550) $("message-input").focus();
  }
}

function selectMemoryTab(tab) {
  state.tab = tab;
  for (const [id, value] of [["tab-active", "Active"], ["tab-pending", "Pending"]]) {
    $(id).classList.toggle("active", tab === value);
    $(id).setAttribute("aria-selected", String(tab === value));
    $(id).tabIndex = tab === value ? 0 : -1;
  }
  $("memory-list").setAttribute("aria-labelledby", tab === "Active" ? "tab-active" : "tab-pending");
  renderMemories();
}

function renderMemories() {
  const active = state.memories.filter(m => m.status === "Active");
  const pending = state.memories.filter(m => m.status === "Pending");
  $("active-count").textContent = active.length;
  $("pending-count").textContent = pending.length;
  $("memory-total").textContent = active.length + pending.length;
  $("memory-toggle").title = `${active.length} guardados · ${pending.length} por revisar`;
  $("tab-pending").classList.toggle("attention", pending.length > 0);
  const list = $("memory-list");
  list.replaceChildren();
  const memories = state.tab === "Active" ? active : pending;
  if (state.memoryError || !memories.length) {
    const empty = element("div", "memory-empty");
    empty.append(icon(state.memoryError ? "refresh" : state.tab === "Active" ? "memory" : "check"));
    empty.append(element("h3", "", state.memoryError ? "No pudimos cargar tu memoria" : state.tab === "Active" ? "Aquí empieza tu memoria" : "Todo al día"));
    empty.append(element("p", "", state.memoryError ? "Vuelve a intentarlo con «Actualizar recuerdos»." : state.tab === "Active" ? "Comparte tus preferencias en el chat. Revisa los recuerdos propuestos y decide cuáles conservar." : "Cuando una conversación aporte algo para recordar, podrás revisarlo aquí."));
    list.append(empty);
  }
  const byId = new Map(state.memories.map(m => [m.id, m]));
  for (const memory of memories) {
    const card = element("article", `memory-card${memory.status === "Pending" ? " pending" : ""}`);
    const header = element("div", "memory-card-header");
    header.append(icon(memory.status === "Active" ? "check" : "spark"), element("span", "", memoryLabel(memory)), element("span", "", memory.status === "Active" ? "Guardado" : "Por revisar"));
    card.append(header, element("p", "", memory.content));
    for (const targetId of memory.replaces) {
      if (byId.has(targetId)) card.append(element("p", "replaces", `Recuerdo relacionado: ${byId.get(targetId).content}`));
    }
    if (memory.sensitive) card.append(element("span", "memory-sensitive", memory.status === "Pending" && memory.operation !== "Delete" ? "Dato sensible: requiere tu consentimiento específico." : "Contiene información sensible."));
    const actions = element("div", "memory-card-actions");
    if (memory.status === "Pending") {
      actions.append(button(memory.operation === "Delete" ? "Revisar olvido" : "Guardar", "approve-button", "check", () => approveMemory(memory)));
      actions.append(button("Descartar", "reject-button", "close", () => rejectMemory(memory)));
    } else {
      actions.append(button("Editar", "edit-button", "edit", () => editMemory(memory)));
      actions.append(button("Olvidar", "forget-button", "trash", () => forgetMemory(memory)));
    }
    for (const node of actions.children) node.disabled = !!state.busy;
    card.append(actions);
    list.append(card);
  }
}

function preview(text) { return element("div", "dialog-content-preview", text); }
function checkbox(id, label, checked = false) {
  const row = element("label", "dialog-checkbox");
  const input = element("input"); input.type = "checkbox"; input.id = id; input.checked = checked;
  row.append(input, document.createTextNode(label));
  return row;
}

function openDialog({ title, description, confirm, danger = false, content = [], action, scrub = false }) {
  if (state.busy) return;
  state.dialog = { action, scrub, working: false, stale: false };
  $("dialog-title").textContent = title;
  $("dialog-description").textContent = description;
  $("dialog-content").replaceChildren(...content);
  $("dialog-error").hidden = true;
  $("dialog-confirm").textContent = confirm;
  $("dialog-confirm").classList.toggle("danger", danger);
  $("dialog-confirm").disabled = false;
  $("action-dialog").showModal();
  $("dialog-cancel").focus();
}

function closeDialog() { if (!state.dialog?.working) $("action-dialog").close(); }

function approveMemory(memory) {
  const deleting = memory.operation === "Delete";
  const content = [preview(memory.content)];
  for (const id of memory.replaces) {
    const target = state.memories.find(item => item.id === id);
    if (target) content.push(element("p", "dialog-label", deleting ? "Recuerdo que se eliminará" : "Recuerdo anterior"), preview(target.content));
  }
  if (memory.sensitive && !deleting) content.push(checkbox("sensitive-consent", "Autorizo guardar este dato sensible y utilizarlo en futuras conversaciones."));
  openDialog({ title: deleting ? "¿Olvidamos este recuerdo?" : "Un recuerdo para la próxima vez",
    description: deleting ? "Se eliminará el recuerdo relacionado y se excluirán sus mensajes de origen y uso. También pueden eliminarse otros recuerdos del mismo turno." : memory.operation === "Replace" ? "Esta propuesta sustituirá los recuerdos relacionados. Revisa el cambio antes de aprobarlo." : "Este dato podrá ayudar al asistente a responderte en otras conversaciones.",
    confirm: deleting ? "Olvidar recuerdo" : "Guardar recuerdo", danger: deleting, content, scrub: deleting || memory.operation === "Replace",
    action: async () => {
      const consent = $("sensitive-consent")?.checked || false;
      if (memory.sensitive && !deleting && !consent) throw new Error("Marca la casilla si autorizas el almacenamiento de este dato sensible.");
      const result = await request(`/api/memory-candidates/${memory.id}/approve`, { method: "POST", body: { version: memory.version, confirmSensitive: consent } });
      return result.warnings?.length ? result.warnings.join(" ") : deleting ? "Recuerdo olvidado." : "Recuerdo guardado.";
    }
  });
}

function rejectMemory(memory) {
  openDialog({ title: "Descartar propuesta", description: "No se guardará como un recuerdo activo. La conversación se conservará.", confirm: "Descartar", content: [preview(memory.content)],
    action: async () => { await request(`/api/memory-candidates/${memory.id}/reject`, { method: "POST", body: { version: memory.version } }); return "Propuesta descartada."; }
  });
}

function forgetMemory(memory) {
  openDialog({ title: "¿Olvidamos este recuerdo?", description: "Se eliminará el recuerdo y su información relacionada. Los turnos que lo originaron o utilizaron dejarán de formar parte del contexto. Otros recuerdos del mismo turno también pueden eliminarse.",
    confirm: "Olvidar recuerdo", danger: true, content: [preview(memory.content)], scrub: true,
    action: async () => { await request(`/api/memories/${memory.id}`, { method: "DELETE" }); return "Recuerdo olvidado."; }
  });
}

function editMemory(memory) {
  const label = element("label", "dialog-label", "¿Qué debería recordar?"); label.htmlFor = "edit-memory-content";
  const input = element("textarea", "dialog-textarea"); input.id = "edit-memory-content"; input.maxLength = 2000; input.value = memory.content; input.required = true;
  const sensitive = checkbox("edit-sensitive", "Este recuerdo contiene información sensible.", memory.sensitive);
  if (memory.sensitive) sensitive.querySelector("input").disabled = true;
  const consent = checkbox("sensitive-consent", "Autorizo guardar datos sensibles en esta corrección, si los contiene.");
  openDialog({ title: "Afinemos este recuerdo", description: "La corrección sustituirá la versión anterior. Si requiere consentimiento, aparecerá en «Por revisar» hasta que lo apruebes.",
    confirm: "Guardar cambios", content: [label, input, sensitive, consent], scrub: true,
    action: async () => {
      const value = input.value.trim();
      if (!value) throw new Error("Escribe el contenido del recuerdo.");
      const isSensitive = $("edit-sensitive").checked;
      const confirmed = $("sensitive-consent").checked;
      if (isSensitive && !confirmed) throw new Error("Confirma el consentimiento específico para guardar este dato sensible.");
      const result = await request(`/api/memories/${memory.id}`, { method: "PATCH", body: { content: value, category: memory.category, version: memory.version, sensitive: isSensitive, confirmSensitive: confirmed } });
      if (result.memory?.status === "Pending") { selectMemoryTab("Pending"); return "Corrección propuesta. Revisa el consentimiento para aprobarla."; }
      return result.warnings?.length ? result.warnings.join(" ") : "Recuerdo actualizado.";
    }
  });
}

function deleteConversation() {
  if (!state.activeId) return;
  const id = state.activeId;
  openDialog({ title: "Eliminar conversación", description: "Se borrarán sus mensajes y los recuerdos derivados. Los recuerdos relacionados pueden dejar de estar disponibles en otras conversaciones. Esta acción no se puede deshacer.",
    confirm: "Eliminar conversación", danger: true, scrub: true,
    action: async () => {
      await request(`/api/conversations/${id}`, { method: "DELETE" });
      state.drafts.delete(id); state.activeId = null; state.conversation = null; persistSelection(); restoreDraftForConversation();
      return "Conversación eliminada.";
    }
  });
}

$("dialog-form").addEventListener("submit", async event => {
  event.preventDefault();
  const dialog = state.dialog;
  if (!dialog || dialog.working || dialog.stale) return;
  dialog.working = true; setBusy("memory");
  $("dialog-confirm").disabled = true; $("dialog-cancel").disabled = true; $("dialog-close").disabled = true;
  $("dialog-error").hidden = true;
  try {
    const message = await dialog.action();
    dialog.working = false;
    $("action-dialog").close();
    await refreshAfterMutation({ scrub: dialog.scrub });
    toast(message);
  } catch (error) {
    $("dialog-error").textContent = error.message;
    $("dialog-error").hidden = false;
    if (error.status === 409 || error.status === 404) {
      dialog.stale = true;
      $("dialog-error").textContent += " Cierra este diálogo y revisa la versión actual antes de continuar.";
      await refreshMemories().catch(() => {});
    }
  } finally {
    dialog.working = false;
    $("dialog-confirm").disabled = dialog.stale;
    $("dialog-cancel").disabled = false; $("dialog-close").disabled = false;
    setBusy(null);
  }
});

$("action-dialog").addEventListener("cancel", event => { if (state.dialog?.working) event.preventDefault(); });
$("action-dialog").addEventListener("close", () => { $("dialog-content").replaceChildren(); state.dialog = null; });
$("dialog-close").addEventListener("click", closeDialog);
$("dialog-cancel").addEventListener("click", closeDialog);
$("chat-form").addEventListener("submit", sendMessage);
$("message-input").addEventListener("input", () => { saveDraft(); updateComposer(); });
$("message-input").addEventListener("keydown", event => {
  if (event.key === "Enter" && !event.shiftKey && !event.isComposing) { event.preventDefault(); if (!state.busy) $("chat-form").requestSubmit(); }
});
$("stop-send").addEventListener("click", () => state.controller?.abort());
$("new-chat").addEventListener("click", newConversation);
$("history-search").addEventListener("input", renderConversations);
$("delete-chat").addEventListener("click", deleteConversation);
$("memory-toggle").addEventListener("click", () => setMemoryOpen($("memory-panel").hidden));
$("close-memory").addEventListener("click", () => { setMemoryOpen(false); $("memory-toggle").focus(); });
$("menu-toggle").addEventListener("click", () => setNavOpen(!document.body.classList.contains("nav-open")));
$("scrim").addEventListener("click", () => { setNavOpen(false); setMemoryOpen(false); $("message-input").focus(); });
$("tab-active").addEventListener("click", () => selectMemoryTab("Active"));
$("tab-pending").addEventListener("click", () => selectMemoryTab("Pending"));
for (const id of ["tab-active", "tab-pending"]) $(id).addEventListener("keydown", event => {
  if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
    event.preventDefault();
    const tab = event.key === "Home" ? "Active" : event.key === "End" ? "Pending" : state.tab === "Active" ? "Pending" : "Active";
    selectMemoryTab(tab); $(tab === "Active" ? "tab-active" : "tab-pending").focus();
  }
});
$("dismiss-notice").addEventListener("click", () => { $("notice").hidden = true; });
$("restore-draft").addEventListener("click", () => {
  const current = $("message-input").value;
  const recovered = current ? `${current}\n\n${state.failedDraft}` : state.failedDraft;
  if (recovered.length > 4000) { toast("El borrador supera los 4000 caracteres. Libera espacio antes de recuperarlo."); return; }
  $("message-input").value = recovered; saveDraft(); updateComposer(); $("message-input").focus();
  $("restore-draft").hidden = true;
});
for (const node of document.querySelectorAll(".suggestion")) node.addEventListener("click", () => {
  if (state.busy) return;
  if ($("message-input").value.trim()) { toast("Tu borrador se conserva. Envíalo o bórralo antes de elegir una sugerencia."); return; }
  $("message-input").value = node.dataset.prompt; saveDraft(); updateComposer(); $("message-input").focus();
});
async function manuallyRefresh(refresh) {
  if (state.busy) return;
  setBusy("loading");
  try { await refresh(); await checkConnection(); }
  catch (error) { showNotice(error.message); }
  finally { setBusy(null); }
}
$("refresh-history").addEventListener("click", () => manuallyRefresh(async () => { await refreshConversations(); await refreshCurrentConversation(); }));
$("refresh-memories").addEventListener("click", () => manuallyRefresh(refreshMemories));
document.addEventListener("keydown", event => {
  if (event.altKey && event.key.toLowerCase() === "n" && !$("action-dialog").open) { event.preventDefault(); newConversation(); }
  if (event.key === "Escape" && !$("action-dialog").open) { setNavOpen(false); if (window.innerWidth < 1280) setMemoryOpen(false); }
});
let wasDesktop = window.innerWidth >= 1280;
window.addEventListener("resize", () => {
  const desktop = window.innerWidth >= 1280;
  if (desktop !== wasDesktop) { setMemoryOpen(desktop); wasDesktop = desktop; }
  if (window.innerWidth > 900) setNavOpen(false);
  else $("sidebar").inert = !document.body.classList.contains("nav-open");
  updateScrim();
});

async function initialize() {
  setMemoryOpen(window.innerWidth >= 1280); setNavOpen(false); selectMemoryTab("Active");
  const restoringConversation = !!state.activeId;
  if (restoringConversation) setBusy("loading");
  const results = await Promise.allSettled([refreshConversations(), refreshMemories(), refreshCurrentConversation(), checkConnection()]);
  const error = results.find(r => r.status === "rejected" && r.reason.status !== 404);
  if (error) showNotice(error.reason.message);
  persistSelection(); renderConversations(); renderMessages();
  if (restoringConversation) setBusy(null);
  else updateComposer();
}
initialize();
