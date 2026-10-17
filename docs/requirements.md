# Technical Requirements: Market Data Collection & Analysis Service

## 1. Overview & Tech Stack
* Build a .NET 10 market data collection and analysis service[cite: 1].
* Collect cryptocurrency price data from the Binance Spot WebSocket API and store it in a relational database[cite: 1].
* Expose an ASP.NET Core HTTP API for querying and analyzing data, with a corresponding console client[cite: 1].

## 2. Market Data Collection
* **Source:** Binance Spot WebSocket API (Multi-stream endpoint for price updates)[cite: 1, 2].
* **Initial Monitored Symbols:** `BTCUSDT`, `ADAUSDT`, and `ETHUSDT`[cite: 2].
* **Storage:** Relational database using Entity Framework Core (e.g., PostgreSQL or SQLite)[cite: 1].

## 3. Configuration & Administration
* Support dynamic management of monitored trading symbols at runtime[cite: 2].
* Provide administrative API endpoints to add, remove, or view configured symbols[cite: 2].

## 4. HTTP API Specifications
* **Content Negotiation:** All endpoints must support both JSON and XML responses based on request headers[cite: 2].
* **Endpoints:**
  * `GET /api/{symbol}/24hAvgPrice`: Returns the average price over the last 24 hours of available data (or over the available period if less than 24 hours)[cite: 2, 3].
  * `GET /api/{symbol}/SimpleMovingAverage?n={numberOfDataPoints}&p={timePeriod}&s={startDateTime}`: Returns the Simple Moving Average (SMA)[cite: 3].
    * Parameters: `symbol` (string), `n` (int), `p` (time period: `1w`, `1d`, `30m`, `5m`, `1m`), `s` (optional starting date/time reference point; defaults to current time if omitted)[cite: 3].

## 5. Console Client & Deployment
* **Console Client:** Must implement the same price analysis capabilities by consuming the HTTP API[cite: 1].
* **Deployment:** Provide a containerized setup (`Dockerfile` / `docker-compose`) along with a `README.md` containing setup, configuration, and database initialization instructions[cite: 3].

## 6. AI-Assisted Development Deliverables
* Maintain an `engineering-notes.md` file detailing approach, key technical decisions, and validation steps[cite: 3].
* Keep a record of significant prompts or instructions used during development[cite: 3].