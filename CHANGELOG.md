# Changelog

All notable changes to this project will be documented in this file.

## [1.1.0] - 2025-11-23

### Added

- **Local Image Server**: Implemented a local HTTP server to serve Stream Deck button icons to InfoPanel via `http://localhost` URLs.
- **Dynamic Sensor Naming**: Sensors are now named based on the actual button title (e.g., "Mute Mic") instead of generic coordinates.
- **Smart Button Management**: Unused buttons are now automatically hidden from the sensor list.
- **Default Icon Resolution**: Added logic to resolve default plugin icons when custom icons are not set.
- **Enhanced Logging**: Integrated comprehensive file-based debug logging across all services. Replaced all console output with structured file logging.
- **Project Cleanup**: Removed unused scripts and updated .gitignore for better project hygiene.

### Fixed

- **Duplicate Devices**: Fixed an issue where the same Stream Deck device would appear multiple times in the device list.
- **Image Path Resolution**: Fixed issues with resolving paths for custom and default icons.

## [1.0.0] - 2024-03-20

### Added

- Initial plugin implementation
- Service-based architecture with MonitoringService, SensorManagementService, ConfigurationService, and FileLoggingService
- Comprehensive configuration management with file-based settings
- Thread-safe sensor updates with proper locking
- User-controllable debug logging with multiple log levels
- Professional build system with ZIP creation and dependency management
- Interactive PowerShell setup script for automated plugin creation
- Data models with validation and formatting
- Comprehensive documentation and TODO comments

### Features

- Event-driven monitoring with async data collection
- Automatic error handling and graceful degradation
- Configurable monitoring intervals and timeouts
- Professional logging with rotation and multiple levels
- Thread-safe updates with collection modification protection
- Build automation with version management and archiving
