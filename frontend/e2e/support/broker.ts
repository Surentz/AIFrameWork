import { request } from '@playwright/test';
import { E2E_RABBITMQ_UI_URL } from './env.ts';

/**
 * Publishes shipment.confirmed.v1 as a warehouse would, through the e2e broker's management HTTP
 * API - no AMQP client dependency in this workspace. Managed stack only: the kind cluster exposes
 * no broker port to the host, which is why the spec using this is @local-only.
 */
export async function publishShipment(orderId: string, shipmentId: string): Promise<void> {
  const context = await request.newContext({
    httpCredentials: { username: 'e2e', password: 'e2e' },
  });

  try {
    const response = await context.post(
      `${E2E_RABBITMQ_UI_URL}/api/exchanges/%2F/amq.default/publish`,
      {
        data: {
          properties: { content_type: 'application/json', delivery_mode: 2 },
          routing_key: 'aiframework.shipments',
          payload: JSON.stringify({ shipmentId, orderId, shippedAt: new Date().toISOString() }),
          payload_encoding: 'string',
        },
      },
    );

    const body = (await response.json()) as { routed?: boolean };
    if (!response.ok() || body.routed !== true) {
      throw new Error(
        `Publishing shipment ${shipmentId} failed with ${String(response.status())}: ${JSON.stringify(body)}`,
      );
    }
  } finally {
    await context.dispose();
  }
}
