// rbinilspy MCP server — delegates to C# worker via JSON-RPC over stdio.

use rbm_server::RbmServer;
use rmcp::service::serve_server;
use rmcp::transport::stdio;

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt()
        .with_env_filter(tracing_subscriber::EnvFilter::from_default_env())
        .init();

    tracing::info!("rbinilspy MCP server starting");

    match RbmServer::new().await {
        Ok(server) => match serve_server(server, stdio()).await {
            Ok(service) => {
                if let Err(error) = service.waiting().await {
                    tracing::error!("server wait error: {error:?}");
                }
            }
            Err(error) => {
                tracing::error!("server error: {error:?}");
            }
        },
        Err(e) => {
            tracing::error!("failed to start server: {e}");
        }
    }
}
