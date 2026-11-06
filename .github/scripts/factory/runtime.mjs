import { executeRuntime } from "./dispatcher.mjs";

try {
  const operation = process.env.FACTORY_OPERATION;
  const contract = JSON.parse(process.env.FACTORY_CONTRACT ?? "");
  await executeRuntime(operation, contract);
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
