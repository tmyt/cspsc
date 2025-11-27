import { getContainer } from "@cloudflare/containers";
import { Hono } from "hono";
import { CompilerService } from "./compiler";

const app = new Hono<{
  Bindings: Env;
}>();

app.post("/compile", async (c) => {
  const containerInstance = getContainer<CompilerService>(
    c.env.COMPILER_SERVICE
  );
  await containerInstance.startAndWaitForPorts();

  const request = new Request("http://internal/", {
    method: c.req.method,
    headers: c.req.raw.headers,
    body: c.req.raw.body,
  });
  return containerInstance.fetch(request);
});

export { app };
