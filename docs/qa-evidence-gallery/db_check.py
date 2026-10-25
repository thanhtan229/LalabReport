import sqlite3
import json

db_path = r'C:\Users\Admin\AppData\Local\LalabAutoReport\lalab_autoreport.db'
conn = sqlite3.connect(db_path)
c = conn.cursor()

c.execute("SELECT id, order_code, status, updated_at FROM orders WHERE id IN (2, 7)")
orders = c.fetchall()
print("Orders 2 and 7:", orders)

c.execute("SELECT id, bill_number, status, locked_at, grand_total, updated_at FROM customer_bills WHERE id IN (SELECT bill_id FROM customer_bill_orders WHERE order_id=2)")
bills = c.fetchall()
print("Bills for order 2:", bills)
