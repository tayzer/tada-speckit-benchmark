@REQ_SHIPMENT_HANDOFF
Feature: Preserve reservation and shipment state

  @AC_SHIPMENT_01
  Scenario: Replayed confirmation is durable
    Given an active reservation of 6 units
    When 2 units are confirmed with key "shift-481"
    Then the confirmation records total 6 and fulfilled 2
    And 4 units are available
    When the inventory API restarts
    And 2 units are confirmed with key "shift-481"
    Then the confirmation records total 6 and fulfilled 2
    And the reservation has total 6 and fulfilled 2
    And 4 units are available

  @AC_SHIPMENT_02
  Scenario: Rejected shipments preserve the reservation
    Given an active reservation of 6 units
    When 2 units are confirmed with key "shift-481"
    Then the confirmation records total 6 and fulfilled 2
    When 3 units are confirmed with key "shift-481"
    Then the last response is 409
    When 5 units are confirmed with key "another-shipment"
    Then the last response is 409
    And the reservation has total 6 and fulfilled 2
    And 4 units are available

  @AC_SHIPMENT_03
  Scenario: Cancellation releases the remaining units once
    Given an active reservation of 6 units
    When 2 units are confirmed with key "shift-481"
    Then the confirmation records total 6 and fulfilled 2
    When the active reservation is cancelled
    Then the last response is 204
    When the active reservation is cancelled
    Then the last response is 204
    And 8 units are available
    When 2 units are confirmed with key "shift-481"
    Then the confirmation records total 6 and fulfilled 2

  @AC_SHIPMENT_04
  Scenario: Legacy inventory is preserved on upgrade
    Given the legacy inventory database
    Then the reservation has total 4 and fulfilled 0
    And 6 units are available
    When 1 units are confirmed with key "legacy-shipment"
    Then the confirmation records total 4 and fulfilled 1
    And 6 units are available

  @AC_SHIPMENT_05
  Scenario: Resize a previously dispatched reservation
    Given an active reservation of 6 units
    When 2 units are confirmed with key "shift-481"
    Then the confirmation records total 6 and fulfilled 2
    When the new reservation total is 4
    Then the last response is 409
    And the reservation has total 6 and fulfilled 2
    And 4 units are available

  @AC_SHIPMENT_06
  Scenario: Legacy clients can still resize an unshipped reservation
    Given an active reservation of 3 units
    When the new reservation total is 5
    Then the last response is 200
    And the reservation has total 5 and fulfilled 0
    And 5 units are available
