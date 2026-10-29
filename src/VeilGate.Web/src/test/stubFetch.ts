import { vi } from "vitest";

export const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status });

export const noContent = () => new Response(null, { status: 204 });

/** Replaces fetch with a fake server. `routes` answers every request; the mock remembers each call. */
export function stubFetch(
  routes: (url: string, init: RequestInit | undefined) => Response,
) {
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) =>
    routes(url, init),
  );
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

/** The JSON body that a call sent. */
export const bodyOf = (init: RequestInit | undefined): unknown =>
  JSON.parse(String(init?.body));

/** The calls whose address ends with `suffix` (and, if given, whose method matches). */
export function callsTo(
  fetchMock: ReturnType<typeof stubFetch>,
  suffix: string,
  method?: string,
) {
  return fetchMock.mock.calls.filter(
    ([url, init]) =>
      url.endsWith(suffix) && (method === undefined || init?.method === method),
  );
}
