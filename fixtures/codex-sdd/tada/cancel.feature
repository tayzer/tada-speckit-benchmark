@REQ_INVENTORY_CANCEL
Feature: Repeat cancellation

  @AC_INVENTORY_CANCEL_01
  Scenario: Retrying cancellation restores stock only once
    Given a reservation of 3 units
    When the reservation is cancelled
    And the reservation is cancelled
    Then both responses have status 204
    And 10 units are available
