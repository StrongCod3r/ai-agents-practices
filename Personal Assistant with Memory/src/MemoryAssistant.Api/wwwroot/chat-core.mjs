export class ApiError extends Error {
  constructor(message, status = 0, code = "connection_error") {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
  }
}

export async function request(path, { method = "GET", body, signal } = {}) {
  if (!signal && method === "GET") signal = AbortSignal.timeout(10000);
  let response;
  try {
    response = await fetch(path, {
      method, signal, cache: "no-store", credentials: "same-origin",
      headers: { Accept: "application/json", ...(body !== undefined ? { "Content-Type": "application/json" } : {}) },
      ...(body !== undefined ? { body: JSON.stringify(body) } : {})
    });
  } catch (error) {
    if (error.name === "AbortError") throw error;
    if (error.name === "TimeoutError") throw new ApiError("La aplicación tarda en responder. Puedes volver a cargar el historial o los recuerdos.", 504, "request_timeout");
    throw new ApiError("No se pudo conectar con el asistente. Comprueba que la aplicación esté iniciada.");
  }
  if (response.status === 204) return null;
  const text = await response.text();
  let data;
  try { data = text ? JSON.parse(text) : null; }
  catch { if (response.ok) throw new ApiError("La aplicación devolvió una respuesta que no se pudo leer.", response.status, "invalid_response"); }
  if (!response.ok) throw new ApiError(data?.detail || "No se pudo completar la solicitud. Vuelve a intentarlo.", response.status, data?.errorCode);
  return data;
}

export function filterConversations(conversations, search) {
  const normalize = value => value.normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLocaleLowerCase("es");
  const term = normalize(search.trim());
  return conversations.filter(item => normalize(item.title).includes(term));
}

export function displayTime(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleTimeString("es", { hour: "2-digit", minute: "2-digit" });
}

export function memoryLabel(memory) {
  if (memory.operation === "Delete") return "Propuesta de olvido";
  if (memory.operation === "Replace") return memory.replaces.length > 1 ? "Propuesta de fusión" : "Propuesta de corrección";
  return memory.category === "Episodic" ? "Una experiencia tuya" : "Algo sobre ti";
}

export function savedConversationId(storage) {
  try {
    const value = storage.getItem("memoria.activeConversation");
    return /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(value || "") ? value : null;
  } catch { return null; }
}
