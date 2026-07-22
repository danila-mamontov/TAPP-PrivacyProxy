This experiment aims to answer RQ2:

> To what extent does interposing the proxy between an agentic AI assistant and the LLM—with PII detection disabled—affect the assistant's success on representative tasks comprising conversation, email communication, and calendar management, compared to direct LLM access?

---

### Experimental Setup
RQ2 is addressed through a technology-oriented controlled experiment using a repeated-measures design: each task in the dataset is executed under both conditions (direct LLM access vs. the intermediary PrivacyProxy, but without(!) detection) with five repetitions each; the agent state is fully reset prior to each measurement.

> You can use the `.puml` diagrams for a quick overview.

**Control Group**.
The control group consists of an OpenClaw server, an LLM server, an MCP server, a MailPit server, and a Radicale server.

OpenClaw is directly connected to the LLM and the MCP server. The MCP server is connected to MailPit and Radicale.

**Experimental Group**.
The experimental group consists of the same components as the control group, plus a PrivacyProxy with a static image from ghcr.io and a Presidio mock that returns only empty lists as detected PIIs in analysis requests (i.e., it does not detect any PIIs).

OpenClaw is connected to the PrivacyProxy and the MCP server, the PrivacyProxy is connected to the Presidio mock and the LLM, and the MCP server is connected to MailPit and Radicale.

---

### Measurement
We measure conversation, email management, and calendar capabilities. Each of these three groups receives 20 prompts from a test dataset. Each prompt is executed with five iterations. Depending on the group, we subsequently validate whether the response, email, or appointment was correctly given/sent/created.

---

### Comparison
We compare the results from the measurement between the experimental group and the control group pairwise with the test groups (conversation, email, calendar) among themselves.