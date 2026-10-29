import type { ReactElement } from "react";
import { render } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { setAccessToken } from "../lib/session";
import { fakeToken } from "./fakeToken";
import Where from "./Where";

type Options = {
  role?: string;
  /** Where the router starts, for example "/clients/c1". */
  path?: string;
  /** The route pattern the page is mounted at, for example "/clients/:id". Defaults to path. */
  route?: string;
};

/**
 * Renders a page the way the app does: signed in as the given role, inside a router and a query client.
 * Any other address shows "elsewhere" plus the query string, so tests can check where the page sent the person.
 */
export function renderPage(element: ReactElement, options: Options = {}) {
  const { role = "TenantAdmin", path = "/", route = path } = options;
  setAccessToken(fakeToken({ role }));
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path={route} element={element} />
          <Route path="*" element={<Where label="elsewhere" />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
