# Ollama model browser

OllamaBrowserClient reuses the engine's OllamaModelLibrary for catalog parsing and NDJSON pulls. Inventory is read separately from the captured Ollama endpoint. Failed inventory requests represent unknown availability, never an empty installed list.

OllamaModelInventory compares exact tags, normalizing only the default library namespace and omitted latest tag. Other registries and namespaces remain distinct.

OllamaBrowserViewModel owns cancellation and stale-response protection independently of WinUI. The non-modal Controls/Models window captures its originating agent and server, cancels requests on close, and switches models only through an explicit action after refreshing inventory. Model switching claims the agent's existing turn gate.
