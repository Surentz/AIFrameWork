import { expect, test } from '../../fixtures/index.ts';
import * as notifications from '../../screens/notifications.ts';
import { publishShipment } from '../../support/broker.ts';
import { uniqueSku } from '../../support/identity.ts';

/**
 * A warehouse confirms a shipment through RabbitMQ, and the buyer sees it (ADR 0026): broker ->
 * worker -> database -> outbox -> notification feed.
 *
 * @local-only: it publishes to the broker from the host, and only the managed stack exposes one.
 */
test(
  'tells the buyer when the warehouse confirms a shipment',
  { tag: '@local-only' },
  async ({ signedInPage, api, workerUser }) => {
    const sku = uniqueSku();

    await test.step('arrange: an order, confirmed shipped by the warehouse', async () => {
      const orderId = await api.placeOrder(workerUser, { sku, quantity: 1 });
      await publishShipment(orderId, `WH-${sku}`);
      await api.waitForNotification(workerUser, { kind: 'OrderShipped', text: sku });
    });

    await notifications.open(signedInPage);

    await expect(
      notifications.item(signedInPage, `Your order for ${sku} is on its way.`),
    ).toContainText('Order shipped');
  },
);
