import importlib.util
import pathlib
import sqlite3
import tempfile
import unittest
from unittest.mock import patch


HELPER = pathlib.Path(__file__).parents[1] / 'maintenance-helper.py'
SPEC = importlib.util.spec_from_file_location('openviya_maintenance_helper', HELPER)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class MaintenanceProtocolChecks(unittest.TestCase):
    def test_database_changes_preserve_base_path_and_other_settings(self):
        with tempfile.TemporaryDirectory() as directory:
            database = pathlib.Path(directory) / 'x-ui.db'
            with sqlite3.connect(database) as db:
                db.execute('CREATE TABLE settings (key TEXT, value TEXT)')
                db.executemany('INSERT INTO settings VALUES (?,?)', [('webPort', '38471'), ('webListen', '127.0.0.1'), ('webBasePath', '/fixture-path/'), ('unrelated', 'retained')])
            db.close()
            with patch.object(MODULE, 'DB', database):
                MODULE.set_panel_db(38472, 'both')
                self.assertEqual(MODULE.panel_db_settings()['webPort'], 38472)
                self.assertEqual(MODULE.panel_db_settings()['webBasePath'], '/fixture-path/')
                with sqlite3.connect(database) as db:
                    self.assertEqual(db.execute("SELECT value FROM settings WHERE key='unrelated'").fetchone()[0], 'retained')
                db.close()

    def test_health_uses_verified_tls_and_rejects_missing_endpoint(self):
        cfg = {'domain': 'panel-fixture.openviya.online', 'panelPort': 38471}
        result = type('Result', (), {'returncode': 0, 'stdout': '200'})()
        with patch.object(MODULE, 'config_values', return_value=cfg), patch.object(MODULE, 'panel_db_settings', return_value={'webBasePath': '/fixture-path/'}), patch.object(MODULE, 'run', return_value=result) as run:
            self.assertTrue(MODULE.panel_healthy())
            args = run.call_args.args[0]
            self.assertIn('--cacert', args)
            self.assertIn('--resolve', args)
            self.assertIn('--noproxy', args)
            self.assertNotIn('--insecure', args)
            self.assertEqual(args[-1], 'https://panel-fixture.openviya.online:38471/fixture-path/')
            result.stdout = '404'
            self.assertFalse(MODULE.panel_healthy())

    def test_ip_list_accepts_canonical_addresses_and_cidrs(self):
        self.assertEqual(MODULE.validate_cidr('192.0.2.7/32\n2001:db8::7/128'), '192.0.2.7/32\n2001:db8::7/128')

    def test_ip_list_deduplicates_and_allows_explicit_any(self):
        self.assertEqual(MODULE.validate_cidr('192.0.2.7/32\n192.0.2.7/32'), '192.0.2.7/32')
        self.assertEqual(MODULE.validate_cidr('any'), 'any')

    def test_ip_list_rejects_noncanonical_or_oversized_values(self):
        for value in ('', 'any\n192.0.2.1/32', '192.0.2.1/24', '192.0.2.1/32\n', '192.0.2.1/32\n\n192.0.2.2/32', '192.0.2.1,192.0.2.2', 'fe80::1%eth0/128'):
            with self.subTest(value=value), self.assertRaises(ValueError):
                MODULE.validate_cidr(value)
        with self.assertRaises(ValueError):
            MODULE.validate_cidr('\n'.join(['192.0.2.1/32'] * 101))

    def test_ufw_inspection_normalizes_host_sources_without_mask(self):
        output = "ufw allow from 192.0.2.7 to any port 38471 proto tcp comment '3x-ui HTTPS admin network'\n"
        self.assertEqual(MODULE.actual_panel_rules(output), {('source', '192.0.2.7/32', 38471, 'tcp', '3x-ui HTTPS admin network')})
        self.assertTrue(MODULE.panel_rules_match_from_output(output, 38471, 'both', '192.0.2.7/32'))
        self.assertFalse(MODULE.panel_rules_match_from_output(output, 38471, 'tunnel', 'any'))

    def test_backup_read_protocol_rejects_path_injection(self):
        for name in ('../../etc/passwd', 'backup-20261017T010203-4.tar.gz;id', 'backup-20261017T010203-4.tar.gz/'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                MODULE.validate_request({'action': 'backup-read', 'name': name, 'offset': 0})

    def test_backup_read_protocol_bounds_offsets(self):
        self.assertEqual(MODULE.validate_request({'action': 'backup-read', 'name': 'backup-20261017T010203-4.tar.gz', 'offset': 0}), 'backup-read')
        for offset in (-1, 2**31 + 1, True):
            with self.subTest(offset=offset), self.assertRaises(ValueError):
                MODULE.validate_request({'action': 'backup-read', 'name': 'backup-20261017T010203-4.tar.gz', 'offset': offset})

    def test_access_change_arms_rollback_before_mutations(self):
        events = []
        class FakeStateFile:
            def write_text(self, _): pass
        class FakeMaintenanceDir:
            def exists(self): return False
            def mkdir(self, **_): pass
            def __truediv__(self, _): return FakeStateFile()
        def fake_run(args, **_):
            events.append('backup' if any(str(item).endswith('openviya-xui-backup') for item in args) else 'timer' if any(str(item).endswith('systemd-run') for item in args) else 'restart')
            return type('Result', (), {'returncode': 0, 'stdout': ''})()
        req = {'operationId': 'a' * 32, 'panelPort': 38472, 'panelAccess': 'tunnel', 'allowedIpsText': 'any'}
        cfg = {'panelPort': 38471, 'panelAccess': 'both', 'allowedIpsText': '192.0.2.7/32', 'domain': 'panel.example.com'}
        with patch.object(MODULE, 'MAINT', FakeMaintenanceDir()), patch.object(MODULE, 'config_values', return_value=cfg), \
             patch.object(MODULE, 'panel_db_settings', return_value={'webPort': 38471, 'webListen': '0.0.0.0'}), \
             patch.object(MODULE, 'rollback_pending', return_value=False), patch.object(MODULE, 'run', side_effect=fake_run), \
             patch.object(MODULE.os, 'chmod'), patch.object(MODULE, 'remove_panel_rules', side_effect=lambda *a: events.append('remove')), \
             patch.object(MODULE, 'write_config', side_effect=lambda *a: events.append('write-config')), \
             patch.object(MODULE, 'set_panel_db', side_effect=lambda *a: events.append('write-db')), \
             patch.object(MODULE, 'add_panel_rules', side_effect=lambda *a: events.append('add')), \
             patch.object(MODULE, 'panel_healthy', return_value=True), patch.object(MODULE, 'panel_rules_match', return_value=True):
            MODULE._apply_access_locked(req, cfg)
        self.assertLess(events.index('timer'), events.index('remove'))
        self.assertLess(events.index('timer'), events.index('write-db'))

    def test_access_change_rolls_back_when_a_mutation_fails(self):
        events = []
        class FakeStateFile:
            def write_text(self, _): pass
        class FakeMaintenanceDir:
            def exists(self): return False
            def mkdir(self, **_): pass
            def __truediv__(self, _): return FakeStateFile()
        def fake_run(args, **_): return type('Result', (), {'returncode': 0, 'stdout': ''})()
        req = {'operationId': 'b' * 32, 'panelPort': 38472, 'panelAccess': 'tunnel', 'allowedIpsText': 'any'}
        cfg = {'panelPort': 38471, 'panelAccess': 'both', 'allowedIpsText': '192.0.2.7/32'}
        with patch.object(MODULE, 'MAINT', FakeMaintenanceDir()), patch.object(MODULE, 'config_values', return_value=cfg), \
             patch.object(MODULE, 'panel_db_settings', return_value={'webPort': 38471, 'webListen': '0.0.0.0'}), \
             patch.object(MODULE, 'rollback_pending', return_value=False), patch.object(MODULE, 'run', side_effect=fake_run), \
             patch.object(MODULE.os, 'chmod'), patch.object(MODULE, 'remove_panel_rules'), \
             patch.object(MODULE, 'write_config', side_effect=RuntimeError('simulated')), \
             patch.object(MODULE, 'rollback', side_effect=lambda operation_id: events.append(operation_id)):
            with self.assertRaisesRegex(RuntimeError, 'simulated'):
                MODULE._apply_access_locked(req, cfg)
        self.assertEqual(events, ['b' * 32])


if __name__ == '__main__':
    unittest.main()
